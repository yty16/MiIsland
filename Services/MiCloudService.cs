using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MiIsland.Models;

namespace MiIsland.Services;

/// <summary>
/// 小米云 API 通信服务（非官方逆向实现，仅供个人学习研究）。
/// 仅通过小米官方提供的「扫码登录」流程授权，不收集、不存储任何账号密码。
/// 详见仓库根目录 DISCLAIMER.md。
///
/// 参考（社区公开逆向研究，仅用于学习）:
/// https://github.com/PiotrMachowski/Xiaomi-cloud-tokens-extractor
/// </summary>
public class MiCloudService : IDisposable
{
    private HttpClientHandler _handler;
    private HttpClient _client;
    private MiSession? _session;
    private bool _disposed;

    // 扫码登录暂存数据
    private string _pendingSsecurity = "";
    private string _pendingUserId = "";
    private string _pendingCUserId = "";
    private string _pendingLocation = "";

    private const string UserAgent = "Android-7.1.1-1.0.0-ONEPLUS A3010-136-1234567890 APP/xiaomi.smarthome APPV/62830";

    // API 端点
    private const string ApiBase = "https://api.io.mi.com/app";

    public MiCloudService()
    {
        _handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        _client = new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    // === 公开API ===

    /// <summary>获取当前登录状态</summary>
    public bool IsLoggedIn => _session?.IsValid == true;
    public string? CurrentUserId => _session?.UserId;

    // 注意：本项目仅支持「扫码登录」（小米官方授权流程），已移除账号密码登录，
    // 因此本服务不处理、不存储任何账号或密码。

    /// <summary>
    /// 获取云端设备列表
    /// </summary>
    public async Task<(List<MiCloudDevice>? Devices, string? Error)> GetDeviceListAsync()
    {
        if (!EnsureSession(out var err)) return (null, err);

        var data = """{"getVirtualModel":true,"getHuamiDevices":1}""";
        var result = await SendCloudApiAsync("/home/device_list", data);

        if (result is null)
            return (null, "获取设备列表失败，请查看日志");

        if (result.Value.TryGetProperty("result", out var resObj) &&
            resObj.TryGetProperty("list", out var list))
        {
            var devices = JsonSerializer.Deserialize<List<MiCloudDevice>>(list.GetRawText());
            return (devices, null);
        }

        if (result.Value.TryGetProperty("code", out var code) && code.GetInt32() != 0)
        {
            var msg = result.Value.TryGetProperty("message", out var m) ? m.GetString() : "未知错误";
            return (null, $"获取设备列表失败: {msg}");
        }

        return (null, "设备列表为空或格式异常");
    }

    /// <summary>
    /// 获取设备属性 (通过云端RPC)
    /// </summary>
    public async Task<Dictionary<string, object?>?> GetDevicePropertiesAsync(
        string did, string[] properties)
    {
        if (!EnsureSession(out _)) return null;

        var rpcData = JsonSerializer.Serialize(new
        {
            id = Random.Shared.Next(1, 100000),
            method = "get_prop",
            @params = properties
        });

        var result = await SendCloudApiAsync($"/home/rpc/{did}", rpcData);
        if (result is null) return null;

        // 尝试多层解析
        JsonElement? propArray = null;

        if (result.Value.TryGetProperty("result", out var directResult))
        {
            if (directResult.ValueKind == JsonValueKind.Array)
                propArray = directResult;
            else if (directResult.TryGetProperty("result", out var innerResult) &&
                     innerResult.ValueKind == JsonValueKind.Array)
                propArray = innerResult;
        }

        if (propArray == null) return null;

        var arr = propArray.Value;
        var dict = new Dictionary<string, object?>();
        for (var i = 0; i < Math.Min(properties.Length, arr.GetArrayLength()); i++)
        {
            var elem = arr[i];
            dict[properties[i]] = elem.ValueKind switch
            {
                JsonValueKind.String => elem.GetString(),
                JsonValueKind.Number => elem.TryGetInt32(out var iv) ? iv : elem.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => elem.GetRawText()
            };
        }
        return dict;
    }

    /// <summary>
    /// 执行云端RPC命令 (set_power, set_bright等)
    /// </summary>
    public async Task<JsonElement?> CallRpcAsync(string did, string method, object? parameters)
    {
        if (!EnsureSession(out _)) return null;

        var rpcData = JsonSerializer.Serialize(new
        {
            id = Random.Shared.Next(1, 100000),
            method,
            @params = parameters ?? Array.Empty<object>()
        });

        var result = await SendCloudApiAsync($"/home/rpc/{did}", rpcData);
        if (result is null) return null;

        // 解析嵌套结果
        if (result.Value.TryGetProperty("result", out var directResult))
        {
            // 如果 result 是数组 ["ok"] 就直接返回
            if (directResult.ValueKind == JsonValueKind.Array)
                return directResult.Clone();

            // 如果 result 包含 result
            if (directResult.TryGetProperty("result", out var innerResult))
                return innerResult.Clone();

            return directResult.Clone();
        }

        return result.Value.Clone();
    }

    /// <summary>
    /// 获取设备完整状态 (一次RPC批量查询)
    /// </summary>
    public async Task<MiDeviceStatus?> GetDeviceStatusAsync(MiCloudDevice device)
    {
        var status = new MiDeviceStatus
        {
            Name = device.Name,
            Did = device.Did,
            IsOnline = device.IsOnline,
            LastUpdated = DateTime.Now
        };

        if (!device.IsOnline)
        {
            status.LastError = "设备离线";
            return status;
        }

        try
        {
            var props = await GetDevicePropertiesAsync(device.Did,
                new[] { "power", "bright", "temperature", "humidity" });

            if (props == null)
            {
                status.LastError = "查询无响应";
                return status;
            }

            if (props.TryGetValue("power", out var powerVal))
                status.IsPoweredOn = powerVal?.ToString() == "on";

            if (props.TryGetValue("bright", out var brightVal))
                status.Brightness = brightVal as int? ?? (int?)Convert.ToInt32(brightVal);

            if (props.TryGetValue("temperature", out var tempVal))
                status.Temperature = tempVal as double? ?? Convert.ToDouble(tempVal);

            if (props.TryGetValue("humidity", out var humVal))
                status.Humidity = humVal as double? ?? Convert.ToDouble(humVal);
        }
        catch (Exception ex)
        {
            status.LastError = ex.Message;
        }

        return status;
    }

    /// <summary>开关电源</summary>
    public async Task<bool> SetPowerAsync(string did, bool turnOn)
    {
        var state = turnOn ? "on" : "off";
        var result = await CallRpcAsync(did, "set_power", new object[] { state });
        if (result == null) return false;

        if (result.Value.ValueKind == JsonValueKind.Array &&
            result.Value.GetArrayLength() > 0)
            return result.Value[0].GetString() == "ok";

        return result.Value.GetRawText().Contains("ok");
    }

    /// <summary>设置亮度 0-100</summary>
    public async Task<bool> SetBrightnessAsync(string did, int brightness)
    {
        var value = Math.Clamp(brightness, 0, 100);
        var result = await CallRpcAsync(did, "set_bright", new object[] { value });
        if (result == null) return false;

        if (result.Value.ValueKind == JsonValueKind.Array &&
            result.Value.GetArrayLength() > 0)
            return result.Value[0].GetString() == "ok";

        return result.Value.GetRawText().Contains("ok");
    }

    /// <summary>登出并清理</summary>
    public void Logout()
    {
        _session = null;
        _handler.CookieContainer = new CookieContainer();
        _qrCts?.Cancel();
        _qrCts?.Dispose();
        _qrCts = null;
    }

    // === 扫码登录 API ===

    private CancellationTokenSource? _qrCts;

    /// <summary>
    /// Step 1: 请求二维码登录信息
    /// 返回二维码图片URL、长轮询URL、超时时间
    /// </summary>
    public async Task<(string? QrImageUrl, string? PollUrl, int Timeout, string Error)?> RequestQrCodeAsync()
    {
        try
        {
            var url = "https://account.xiaomi.com/longPolling/loginUrl";
            var dc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var resp = await _client.GetAsync($"{url}?_qrsize=480&qs=%3Fsid%3Dxiaomiio%26_json%3Dtrue&callback=https://sts.api.io.mi.com/sts&_hasLogo=false&sid=xiaomiio&serviceParam=&_locale=zh_CN&_dc={dc}");
            var body = await resp.Content.ReadAsStringAsync();
            var data = ParseXiaomiJson(body);

            var qr = data.TryGetProperty("qr", out var q) ? q.GetString() : null;
            var lp = data.TryGetProperty("lp", out var p) ? p.GetString() : null;
            var timeout = data.TryGetProperty("timeout", out var t) ? t.GetInt32() : 180;

            if (string.IsNullOrEmpty(qr) || string.IsNullOrEmpty(lp))
                return (null, null, 0, "获取二维码失败，请检查网络连接");

            return (qr, lp, timeout, "");
        }
        catch (Exception ex)
        {
            return (null, null, 0, $"请求异常: {ex.Message}");
        }
    }

    /// <summary>
    /// Step 2+3: 长轮询等待用户扫码
    /// 返回: (是否成功, 状态消息)
    /// 成功时内部已设置 _session 的前置数据
    /// </summary>
    public async Task<(bool Success, string Message, string? Location)> PollQrLoginAsync(
        string pollUrl, int timeoutSeconds,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var timeoutSpan = TimeSpan.FromSeconds(timeoutSeconds);
            // 使用合并的 CancellationToken
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _qrCts?.Token ?? default);

            while (true)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                if (DateTime.UtcNow - startTime > timeoutSpan)
                    return (false, "二维码已过期，请重新获取", null);

                try
                {
                    var resp = await _client.GetAsync(pollUrl, linkedCts.Token)
                        .WaitAsync(TimeSpan.FromSeconds(15));

                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        var data = ParseXiaomiJson(body);

                        // 检查是否有错误
                        if (data.TryGetProperty("code", out var code) &&
                            code.ValueKind != JsonValueKind.Null)
                        {
                            var codeVal = code.ValueKind == JsonValueKind.Number
                                ? code.GetInt32()
                                : (code.ValueKind == JsonValueKind.String ? int.Parse(code.GetString()!) : 0);
                            if (codeVal != 0)
                            {
                                var msg = data.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String
                                    ? d.GetString() : "未知错误";
                                return (false, $"扫码失败: {msg}", null);
                            }
                        }

                        var userId = SafeGetString(data, "userId");
                        var ssecurity = SafeGetString(data, "ssecurity");
                        var cUserId = string.IsNullOrEmpty(SafeGetString(data, "cUserId")) ? userId : SafeGetString(data, "cUserId");
                        var location = SafeGetString(data, "loc")
                            ?? SafeGetString(data, "location");

                        if (string.IsNullOrEmpty(location))
                            return (false, "未获取到登录跳转地址", null);

                        // 暂存数据，待 step4 完成登录
                        _pendingSsecurity = ssecurity ?? "";
                        _pendingUserId = userId ?? "";
                        _pendingCUserId = cUserId ?? "";
                        _pendingLocation = location ?? "";

                        return (true, "扫码成功，正在完成登录...", location);
                    }

                    // 其他状态码：继续等待
                    await Task.Delay(2000, linkedCts.Token);
                }
                catch (TaskCanceledException)
                {
                    if (DateTime.UtcNow - startTime > timeoutSpan)
                        return (false, "二维码已过期，请重新获取", null);
                    // 超时重试
                    continue;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(3000, linkedCts.Token);
                    continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return (false, "已取消扫码登录", null);
        }
        catch (Exception ex)
        {
            return (false, $"轮询异常: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Step 4: 完成扫码登录 - 跟随location重定向获取 serviceToken
    /// 参考 Xiaomi-cloud-tokens-extractor QrCodeXiaomiCloudConnector.login_step_4
    /// </summary>
    public async Task<(bool Success, string Message)> CompleteQrLoginAsync(string location)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[MiCloud] Step4: fetching location={location}");

            // 小米 step4 通常需要向 location 提交一个空的 form 请求，
            // content-type 必须设置在 HttpContent 上，不能加在 request headers 上。
            var req = new HttpRequestMessage(HttpMethod.Post, location);
            var postContent = new StringContent("", Encoding.UTF8);
            postContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            req.Content = postContent;

            var locResp = await _client.SendAsync(req);
            await locResp.Content.ReadAsStringAsync(); // consume body

            System.Diagnostics.Debug.WriteLine($"[MiCloud] Step4 response status={locResp.StatusCode}");

            // 兜底：显式请求 STS 端点，确保 api.io.mi.com 域的 serviceToken 被设置
            try
            {
                await _client.GetAsync("https://sts.api.io.mi.com/sts?sid=xiaomiio&_json=true");
                System.Diagnostics.Debug.WriteLine("[MiCloud] STS exchange requested");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MiCloud] STS exchange failed: {ex.Message}");
            }

            // 1) 优先从 api.io.mi.com / sts.api.io.mi.com 域获取
            string? serviceToken = TryGetCookie("https://api.io.mi.com", "serviceToken")
                                 ?? TryGetCookie("https://sts.api.io.mi.com", "serviceToken");

            // 2) 兜底：扫描所有已存储 cookie 找任意 serviceToken
            if (string.IsNullOrEmpty(serviceToken))
            {
                try
                {
                    foreach (System.Net.Cookie c in _handler.CookieContainer.GetAllCookies())
                    {
                        if (c.Name == "serviceToken" && !string.IsNullOrEmpty(c.Value))
                        {
                            serviceToken = c.Value;
                            System.Diagnostics.Debug.WriteLine($"[MiCloud] Found serviceToken on {c.Domain}");
                            break;
                        }
                    }
                }
                catch { }
            }

            // 3) 再兜底：从 location 响应的 Set-Cookie 头里提取
            if (string.IsNullOrEmpty(serviceToken))
            {
                foreach (var header in locResp.Headers)
                {
                    if (!header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var val in header.Value)
                    {
                        if (!val.Contains("serviceToken=", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var part in val.Split(';'))
                        {
                            var kv = part.Trim().Split('=', 2);
                            if (kv.Length == 2 && kv[0].Trim() == "serviceToken")
                            {
                                serviceToken = kv[1].Trim();
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(serviceToken)) break;
                    }
                    if (!string.IsNullOrEmpty(serviceToken)) break;
                }
            }

            if (string.IsNullOrEmpty(serviceToken))
                return (false, "获取服务令牌失败（扫码可能被服务端拒绝，请重试）");

            // 将 serviceToken（及 userId）注入到设备接口所需的域，保证后续请求一定带 cookie
            InjectCookie("serviceToken", serviceToken);
            if (!string.IsNullOrEmpty(_pendingUserId))
                InjectCookie("userId", _pendingUserId);

            _session = new MiSession
            {
                UserId = _pendingUserId,
                ServiceToken = serviceToken,
                Ssecurity = _pendingSsecurity,
                CUserId = _pendingCUserId,
                ExpiresAt = DateTime.Now.AddDays(7)
            };

            System.Diagnostics.Debug.WriteLine($"[MiCloud] QR Login success: userId={_pendingUserId}");
            return (true, "扫码登录成功");
        }
        catch (Exception ex)
        {
            return (false, $"完成登录失败: {ex.Message}");
        }
        finally
        {
            _pendingSsecurity = "";
            _pendingUserId = "";
            _pendingCUserId = "";
            _pendingLocation = "";
        }
    }

    /// <summary>
    /// 取消正在进行的扫码登录
    /// </summary>
    public void CancelQrLogin()
    {
        _qrCts?.Cancel();
        _qrCts = null;
    }

    /// <summary>
    /// 下载图片流 (用于扫码登录二维码显示)
    /// </summary>
    public async Task<System.IO.Stream> DownloadImageAsync(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var ms = new MemoryStream();
        await response.Content.CopyToAsync(ms);
        ms.Position = 0;
        return ms;
    }

    // === 私有实现 ===

    private bool EnsureSession(out string? error)
    {
        if (!IsLoggedIn)
        {
            error = "未登录，请先在设置中登录小米账号";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>设置云端API请求头</summary>
    private void SetCloudHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("x-xiaomi-protocol-flag-cli",
            "PROTOCAL-HTTP2");
        // miot 接口需要 Authorization: serviceToken=XXX 头（与参考实现一致）
        if (!string.IsNullOrEmpty(_session?.ServiceToken))
            request.Headers.TryAddWithoutValidation("Authorization",
                $"serviceToken={_session.ServiceToken}");
        // Cookie 由 CookieContainer 自动管理
    }

    /// <summary>
    /// 解析小米JSONP响应：响应体常以 "&&&START&&&" 前缀包裹，需剥离前缀再从第一个 '{' 解析
    /// </summary>
    private static JsonElement ParseXiaomiJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return default;
        var start = raw.IndexOf('{');
        var json = start >= 0 ? raw.Substring(start) : raw;
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    /// <summary>从指定域名读取 cookie 值（找不到返回 null）</summary>
    private string? TryGetCookie(string domain, string name)
    {
        try
        {
            return _handler.CookieContainer.GetCookies(new Uri(domain))[name]?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 将 cookie 注入到设备接口所需的各域名，确保后续 API 请求一定携带
    /// （小米 serviceToken 经 STS 登录后用于 api.io.mi.com）
    /// </summary>
    private void InjectCookie(string name, string value)
    {
        var targets = new[]
        {
            "api.io.mi.com",
            "sts.api.io.mi.com",
            "account.xiaomi.com",
            "xiaomi.com"
        };
        foreach (var host in targets)
        {
            try
            {
                _handler.CookieContainer.Add(new Uri($"https://{host}/"),
                    new Cookie(name, value, "/", host));
            }
            catch { /* 忽略无效域名 */ }
        }
    }

    /// <summary>
    /// 安全获取 JSON 字段为字符串（兼容 String 和 Number 类型）
    /// </summary>
    private static string? SafeGetString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var elem))
            return null;
        return elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.GetRawText(), // 保持原样（数字原文本）
            _ => elem.ValueKind == JsonValueKind.Null ? null : elem.ToString()
        };
    }

    /// <summary>
    /// 统一调用小米云端 API（自动处理签名、RC4 加密/解密、日志）
    /// </summary>
    private async Task<JsonElement?> SendCloudApiAsync(string path, string dataJson)
    {
        var url = $"{ApiBase}{path}";
        var nonce = GenerateNonce();
        var signedNonce = SignNonce(nonce);
        var key = Convert.FromBase64String(signedNonce);

        // 1) 原始 params，先计算 rc4_hash__（保持固定顺序：data, rc4_hash__）
        var rawParams = new List<KeyValuePair<string, string>>
        {
            new("data", dataJson)
        };
        rawParams.Add(new KeyValuePair<string, string>("rc4_hash__",
            GenerateCloudSignature(url, signedNonce, rawParams)));

        // 2) RC4 加密所有值（_signature 稍后加，不参与加密）
        var encryptedParams = new List<KeyValuePair<string, string>>();
        foreach (var kv in rawParams)
        {
            var encrypted = Rc4Crypt(key, Encoding.UTF8.GetBytes(kv.Value));
            encryptedParams.Add(new KeyValuePair<string, string>(kv.Key, Convert.ToBase64String(encrypted)));
        }

        // 3) 对加密后的 params 计算 _signature
        encryptedParams.Add(new KeyValuePair<string, string>("_signature",
            GenerateCloudSignature(url, signedNonce, encryptedParams)));

        // 4) 确保 data 字段存在（空字符串也可）
        if (!encryptedParams.Any(kv => kv.Key == "data"))
            encryptedParams.Add(new KeyValuePair<string, string>("data", ""));

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        SetCloudHeaders(request);

        var content = new FormUrlEncodedContent(encryptedParams);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Content = content;

        System.Diagnostics.Debug.WriteLine($"[MiCloud] API {path}: data={dataJson}");

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        System.Diagnostics.Debug.WriteLine($"[MiCloud] API {path} response ({response.StatusCode}): {body[..Math.Min(body.Length, 500)]}");

        try
        {
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            string plainText;

            // 小米 cloud API 通常返回 text/html 或 text/plain 的 RC4 加密数据，前缀 &&&START&&&
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                plainText = body;
            }
            else
            {
                var prefix = "&&&START&&&";
                var base64Text = body.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? body[prefix.Length..]
                    : body;
                var cipher = Convert.FromBase64String(base64Text);
                plainText = Encoding.UTF8.GetString(Rc4Crypt(key, cipher));
                System.Diagnostics.Debug.WriteLine($"[MiCloud] API {path} decrypted: {plainText[..Math.Min(plainText.Length, 500)]}");
            }

            return JsonSerializer.Deserialize<JsonElement>(plainText);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiCloud] API {path} parse error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 生成小米 cloud API 签名
    /// signature = base64(sha1("POST" + path + "?" + queryString + signedNonce))
    /// 其中 path 为 URL 去掉 https://api.io.mi.com/app 后的部分
    /// </summary>
    private static string GenerateCloudSignature(string url, string signedNonce,
        List<KeyValuePair<string, string>> parameters)
    {
        var path = url.Replace(ApiBase, ""); // e.g. /home/device_list
        var pairs = parameters.Select(kv => $"{kv.Key}={kv.Value}");
        var signatureString = string.Join("&", pairs);
        var s = $"POST{path}?{signatureString}{signedNonce}";

        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToBase64String(hash);
    }

    /// <summary>RC4 对称加解密</summary>
    private static byte[] Rc4Crypt(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) % 256;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var x = 0;
        var y = 0;
        var result = new byte[data.Length];
        for (var k = 0; k < data.Length; k++)
        {
            x = (x + 1) % 256;
            y = (y + s[x]) % 256;
            (s[x], s[y]) = (s[y], s[x]);
            result[k] = (byte)(data[k] ^ s[(s[x] + s[y]) % 256]);
        }
        return result;
    }

    /// <summary>生成随机 nonce（与小米协议一致：time + random）</summary>
    private static string GenerateNonce()
    {
        var millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var timeBytes = BitConverter.GetBytes(millis / 60000);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(timeBytes);

        var random = new byte[8];
        Random.Shared.NextBytes(random);

        var nonce = new byte[16];
        Array.Copy(timeBytes, 0, nonce, 0, 8);
        Array.Copy(random, 0, nonce, 8, 8);
        return Convert.ToBase64String(nonce);
    }

    /// <summary>
    /// 用 ssecurity 签名 nonce: base64(sha1(ssecurity_bytes + nonce_bytes))
    /// </summary>
    private string SignNonce(string nonce)
    {
        var ssecBytes = Convert.FromBase64String(_session?.Ssecurity ?? "");
        var nonceBytes = Convert.FromBase64String(nonce);
        var combined = new byte[ssecBytes.Length + nonceBytes.Length];
        Array.Copy(ssecBytes, 0, combined, 0, ssecBytes.Length);
        Array.Copy(nonceBytes, 0, combined, ssecBytes.Length, nonceBytes.Length);

        var hash = SHA1.HashData(combined);
        return Convert.ToBase64String(hash);
    }

    // 备注：账号密码登录已移除，相关 MD5 逻辑一并删除，本插件不再处理任何密码。

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
        _handler?.Dispose();
        GC.SuppressFinalize(this);
    }
}
