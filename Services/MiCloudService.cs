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
/// 通过小米账号登录，使用云端 API 查询和控制米家设备。
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

    // API 端点 (中国大陆)
    private const string LoginSignUrl = "https://account.xiaomi.com/pass/serviceLogin?sid=xiaomiio&_json=true";
    private const string LoginAuthUrl = "https://account.xiaomi.com/pass/serviceLoginAuth2";
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

    /// <summary>
    /// 使用小米账号密码登录
    /// </summary>
    /// <returns>登录成功返回true; 需要验证码时返回false并提示</returns>
    public async Task<(bool Success, string Message)> LoginAsync(string username, string password, string country = "cn")
    {
        try
        {
            // Step 1: 获取登录页面的 _sign
            var signResp = await _client.GetStringAsync(LoginSignUrl);
            var signData = ParseXiaomiJson(signResp);

            if (!signData.TryGetProperty("_sign", out var sign))
                return (false, "获取登录签名失败，请稍后重试");

            if (signData.TryGetProperty("notificationUrl", out var notif))
            {
                var notifStr = notif.GetString();
                if (!string.IsNullOrEmpty(notifStr))
                    return (false, $"需要验证码验证，请先在手机上登录小米账号，再尝试 {notifStr}");
            }

            var qs = signData.TryGetProperty("qs", out var q) ? q.GetString() : "";
            var callback = signData.TryGetProperty("callback", out var cb) ? cb.GetString() : "";

            // Step 2: 提交登录凭据
            var passwordHash = ComputeMd5Upper(password);
            var loginBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["sid"] = "xiaomiio",
                ["hash"] = passwordHash,
                ["user"] = username,
                ["_sign"] = sign.GetString()!,
                ["_json"] = "true",
                ["qs"] = qs ?? "",
                ["callback"] = callback ?? "",
                ["serviceParam"] = """{"checkSafePhone":false}"""
            });

            var authResp = await _client.PostAsync(LoginAuthUrl, loginBody);
            var authBody = await authResp.Content.ReadAsStringAsync();
            var authData = ParseXiaomiJson(authBody);

            // 检查是否需要验证码
            if (authData.TryGetProperty("notificationUrl", out var authNotify))
            {
                var aNotifStr = authNotify.GetString();
                if (!string.IsNullOrEmpty(aNotifStr))
                    return (false, $"账号需要安全验证，请先在手机小米商城App中登录一次");
            }

            var code = authData.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            if (code != 0)
            {
                var msg = authData.TryGetProperty("desc", out var d) ? d.GetString() : "未知错误";
                return (false, $"登录失败: {msg}");
            }

            var ssecurity = authData.GetProperty("ssecurity").GetString()!;
            var userId = authData.GetProperty("userId").GetString()!;
            var cUserId = authData.TryGetProperty("cUserId", out var cu) ? cu.GetString() ?? userId : userId;
            var location = authData.GetProperty("location").GetString()!;

            // Step 3: 获取 serviceToken (通过跟随location重定向)
            var locationResp = await _client.GetAsync(location);
            await locationResp.Content.ReadAsStringAsync(); // consume

            // 从CookieContainer中提取 serviceToken（优先 api.io.mi.com / sts 域，兜底扫描所有）
            var serviceToken = TryGetCookie("https://api.io.mi.com", "serviceToken")
                ?? TryGetCookie("https://sts.api.io.mi.com", "serviceToken");

            if (string.IsNullOrEmpty(serviceToken))
            {
                try
                {
                    foreach (System.Net.Cookie ck in _handler.CookieContainer.GetAllCookies())
                    {
                        if (ck.Name == "serviceToken" && !string.IsNullOrEmpty(ck.Value))
                        {
                            serviceToken = ck.Value;
                            break;
                        }
                    }
                }
                catch { }
            }

            var cookieUserId = TryGetCookie("https://api.io.mi.com", "userId")
                ?? TryGetCookie("https://account.xiaomi.com", "userId")
                ?? userId;

            if (string.IsNullOrEmpty(serviceToken))
                return (false, "获取服务令牌失败，请重试");

            // 注入到设备接口所需域，确保请求一定带 cookie
            InjectCookie("serviceToken", serviceToken);
            if (!string.IsNullOrEmpty(cookieUserId))
                InjectCookie("userId", cookieUserId);

            _session = new MiSession
            {
                UserId = cookieUserId,
                ServiceToken = serviceToken,
                Ssecurity = ssecurity,
                CUserId = cUserId,
                ExpiresAt = DateTime.Now.AddDays(7) // token 一般有效期7天
            };

            System.Diagnostics.Debug.WriteLine($"[MiCloud] Login success: userId={cookieUserId}");
            return (true, "登录成功");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"网络错误: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"登录异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取云端设备列表
    /// </summary>
    public async Task<(List<MiCloudDevice>? Devices, string? Error)> GetDeviceListAsync()
    {
        if (!EnsureSession(out var err)) return (null, err);

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{ApiBase}/home/device_list");

            SetCloudHeaders(request);

            // 添加带签名的 data 参数 (GET请求拼在URL上)
            var queryData = """{"getVirtualModel":true,"getHuamiDevices":0}""";
            var signedUrl = SignRequestUrl($"{ApiBase}/home/device_list", queryData);
            request.RequestUri = new Uri(signedUrl);

            var response = await _client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<JsonElement>(body);

            if (result.TryGetProperty("result", out var resObj) &&
                resObj.TryGetProperty("list", out var list))
            {
                var devices = JsonSerializer.Deserialize<List<MiCloudDevice>>(list.GetRawText());
                return (devices, null);
            }

            if (result.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            {
                var msg = result.TryGetProperty("message", out var m) ? m.GetString() : "未知错误";
                return (null, $"获取设备列表失败: {msg}");
            }

            return (null, "设备列表为空或格式异常");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"网络请求失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取设备属性 (通过云端RPC)
    /// </summary>
    public async Task<Dictionary<string, object?>?> GetDevicePropertiesAsync(
        string did, string[] properties)
    {
        if (!EnsureSession(out _)) return null;

        try
        {
            // 使用 RPC 方式查询属性
            var rpcData = JsonSerializer.Serialize(new
            {
                id = Random.Shared.Next(1, 100000),
                method = "get_prop",
                @params = properties
            });

            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{ApiBase}/home/rpc/{did}");

            SetCloudHeaders(request);
            SetSignedRequestData(request, rpcData);

            var response = await _client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<JsonElement>(body);

            // 尝试多层解析
            JsonElement? propArray = null;

            // 直接 result
            if (result.TryGetProperty("result", out var directResult))
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
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 执行云端RPC命令 (set_power, set_bright等)
    /// </summary>
    public async Task<JsonElement?> CallRpcAsync(string did, string method, object? parameters)
    {
        if (!EnsureSession(out _)) return null;

        try
        {
            var rpcData = JsonSerializer.Serialize(new
            {
                id = Random.Shared.Next(1, 100000),
                method,
                @params = parameters ?? Array.Empty<object>()
            });

            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{ApiBase}/home/rpc/{did}");

            SetCloudHeaders(request);
            SetSignedRequestData(request, rpcData);

            var response = await _client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(body);

            // 解析嵌套结果
            if (result.TryGetProperty("result", out var directResult))
            {
                // 如果 result 是数组 ["ok"] 就直接返回
                if (directResult.ValueKind == JsonValueKind.Array)
                    return directResult.Clone();

                // 如果 result 包含 result
                if (directResult.TryGetProperty("result", out var innerResult))
                    return innerResult.Clone();

                return directResult.Clone();
            }

            return result.Clone();
        }
        catch
        {
            return null;
        }
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
                        _pendingSsecurity = ssecurity;
                        _pendingUserId = userId;
                        _pendingCUserId = cUserId;
                        _pendingLocation = location;

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

            // 关键：必须带 content-type 头（与参考实现一致）
            var req = new HttpRequestMessage(HttpMethod.Get, location);
            req.Headers.Add("content-type", "application/x-www-form-urlencoded");

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
    /// 对请求URL进行签名 (GET请求)
    /// </summary>
    private string SignRequestUrl(string url, string jsonData)
    {
        var nonce = GenerateNonce();
        var signedNonce = SignNonce(nonce);
        var encoded = Uri.EscapeDataString(jsonData);

        return $"{url}?data={encoded}&rc4_hash__={signedNonce}" +
               $"&sign={signedNonce}" +
               $"&ssecurity={_session?.Ssecurity}" +
               $"&_nonce={nonce}";
    }

    /// <summary>
    /// 对POST请求的data字段进行签名
    /// </summary>
    private void SetSignedRequestData(HttpRequestMessage request, string jsonData)
    {
        var nonce = GenerateNonce();
        var signedNonce = SignNonce(nonce);

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["data"] = jsonData,
            ["sign"] = signedNonce,
            ["_nonce"] = nonce,
            ["ssecurity"] = _session?.Ssecurity ?? ""
        });
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        request.Content = content;
    }

    /// <summary>生成随机nonce</summary>
    private static string GenerateNonce()
    {
        var random = new byte[8];
        Random.Shared.NextBytes(random);
        var millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var timeBytes = BitConverter.GetBytes(millis / 60000);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(timeBytes);

        var nonce = new byte[16];
        Array.Copy(random, 0, nonce, 0, 8);
        Array.Copy(timeBytes, 0, nonce, 8, 8);
        return Convert.ToBase64String(nonce);
    }

    /// <summary>用ssecurity签名nonce: base64(sha1(nonce_bytes + ssecurity_bytes))</summary>
    private string SignNonce(string nonce)
    {
        var nonceBytes = Convert.FromBase64String(nonce);
        var ssecBytes = Encoding.UTF8.GetBytes(_session?.Ssecurity ?? "");
        var combined = new byte[nonceBytes.Length + ssecBytes.Length];
        Array.Copy(nonceBytes, 0, combined, 0, nonceBytes.Length);
        Array.Copy(ssecBytes, 0, combined, nonceBytes.Length, ssecBytes.Length);

        var hash = SHA1.HashData(combined);
        return Convert.ToBase64String(hash);
    }

    /// <summary>MD5大写 (小米登录用的hash格式)</summary>
    private static string ComputeMd5Upper(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash); // 默认大写
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
        _handler?.Dispose();
        GC.SuppressFinalize(this);
    }
}
