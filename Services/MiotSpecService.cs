using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiIsland.Models;

namespace MiIsland.Services;

/// <summary>
/// 拉取并缓存设备的 miot-spec 实例定义（services / properties / actions 及其 siid/piid/aiid）。
/// 这是「按设备类型设计差异化控制」的数据来源：每个设备的真实可控能力都在 spec 里。
///
/// 数据源：miot-spec.org 公共实例 API（行业标准）。
///   - 首次启动拉一次「全量 model→URN 映射」: /miot-spec-v2/instances?status=all
///   - 后续按 model 查 URN，再用 URN 拉实例: /miot-spec-v2/instance?type={URN}
/// 缓存：
///   - 映射 →  插件目录/miot_specs/urn-mapping.json
///   - 设备 →  插件目录/miot_specs/instances/{urn}.json
///   - 用户可把离线 json 放进对应目录直接供给。
/// 失败安全：拉不到返回 null，调用方回退基础开关控制。
/// </summary>
public static class MiotSpecService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    static MiotSpecService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
    }

    private static string CacheRoot
    {
        get
        {
            var dir = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                ?? AppContext.BaseDirectory,
                "miot_specs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string CacheDir => Path.Combine(CacheRoot, "instances");

    private const string MappingUrl = "https://miot-spec.org/miot-spec-v2/instances?status=all";
    private const string InstanceUrl = "https://miot-spec.org/miot-spec-v2/instance?type={0}";
    private const string FallbackUrl = "https://raw.githubusercontent.com/Maxmudov/miot-specs/master/{0}.json";

    private static readonly SemaphoreSlim MappingLock = new(1, 1);
    private static Dictionary<string, string>? _urnByModel;
    private static readonly Dictionary<string, MiotSpec?> MemCache = new();

    public static async Task<MiotSpec?> GetSpecAsync(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (MemCache.TryGetValue(model, out var cached)) return cached;

        var urn = await ResolveUrnAsync(model);
        if (string.IsNullOrEmpty(urn))
        {
            MemCache[model] = null;
            return null;
        }

        var instFile = Path.Combine(CacheDir, Sanitize(urn) + ".json");
        if (File.Exists(instFile))
        {
            try
            {
                var fromDisk = Parse(File.ReadAllText(instFile));
                if (fromDisk != null) { MemCache[model] = fromDisk; return fromDisk; }
            }
            catch { /* 文件损坏继续走网络 */ }
        }

        var url = string.Format(InstanceUrl, Uri.EscapeDataString(urn));
        var json = await TryFetchAsync(url);
        if (!string.IsNullOrEmpty(json))
        {
            var spec = Parse(json);
            if (spec != null)
            {
                TryWrite(instFile, json);
                MemCache[model] = spec;
                return spec;
            }
        }

        var fbUrl = string.Format(FallbackUrl, Uri.EscapeDataString(model));
        var fb = await TryFetchAsync(fbUrl);
        if (!string.IsNullOrEmpty(fb))
        {
            var spec = Parse(fb);
            if (spec != null)
            {
                TryWrite(instFile, fb);
                MemCache[model] = spec;
                return spec;
            }
        }

        MemCache[model] = null;
        return null;
    }

    /// <summary>
    /// 解析 model → URN。三级：内存 → 本地 mapping.json → 在线 mapping 缓存到本地。
    /// </summary>
    private static async Task<string?> ResolveUrnAsync(string model)
    {
        await EnsureMappingLoadedAsync();
        return _urnByModel != null && _urnByModel.TryGetValue(model, out var urn) ? urn : null;
    }

    private static async Task EnsureMappingLoadedAsync()
    {
        if (_urnByModel != null) return;

        await MappingLock.WaitAsync();
        try
        {
            if (_urnByModel != null) return;

            var mapFile = Path.Combine(CacheRoot, "urn-mapping.json");
            Dictionary<string, string>? dict = null;

            if (File.Exists(mapFile))
            {
                try { dict = ParseMapping(File.ReadAllText(mapFile)); } catch { /* 损坏就重新拉 */ }
            }

            if (dict == null)
            {
                var json = await TryFetchAsync(MappingUrl);
                if (!string.IsNullOrEmpty(json))
                {
                    dict = ParseMapping(json);
                    if (dict != null) TryWrite(mapFile, json);
                }
            }

            _urnByModel = dict ?? new Dictionary<string, string>();
        }
        finally { MappingLock.Release(); }
    }

    private static Dictionary<string, string>? ParseMapping(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("instances", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in arr.EnumerateArray())
            {
                if (e.TryGetProperty("model", out var m) && e.TryGetProperty("type", out var t)
                    && m.ValueKind == JsonValueKind.String && t.ValueKind == JsonValueKind.String)
                {
                    var model = m.GetString();
                    var urn = t.GetString();
                    if (!string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(urn) && !d.ContainsKey(model))
                        d[model] = urn!;
                }
            }
            return d.Count > 0 ? d : null;
        }
        catch { return null; }
    }

    private static async Task<string?> TryFetchAsync(string url)
    {
        try
        {
            var s = await HttpClient.GetStringAsync(url);
            if (string.IsNullOrWhiteSpace(s) || s.Length < 20
                || s.Contains("data not found", StringComparison.OrdinalIgnoreCase))
                return null;
            return s;
        }
        catch { return null; }
    }

    private static MiotSpec? Parse(string json)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<MiotSpec>(json);
            if (spec == null || spec.Services.Count == 0) return null;
            foreach (var svc in spec.Services)
            {
                svc.Properties ??= new();
                svc.Actions ??= new();
            }
            return spec;
        }
        catch { return null; }
    }

    private static void TryWrite(string path, string content)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        catch { /* 缓存失败不致命 */ }
    }

    /// <summary>从 URN 提取服务短名（service:xxx:hex → xxx），用于按服务类型排版/过滤。</summary>
    public static string ServiceShortName(string urn)
    {
        if (string.IsNullOrEmpty(urn)) return "";
        var s = urn;
        var idx = s.LastIndexOf(':');
        if (idx > 0 && idx + 1 < s.Length && IsHex8(s[(idx + 1)..])) s = s[..idx];
        idx = s.LastIndexOf(':');
        return idx < 0 ? s : s[(idx + 1)..];
    }

    private static bool IsHex8(string s) => s.Length == 8 && Uri.IsHexDigit(s[0]) && Uri.IsHexDigit(s[7]);

    private static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
