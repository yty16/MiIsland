using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MiIsland.Models;

namespace MiIsland.Services;

/// <summary>
/// 拉取并缓存设备的 miot-spec 实例定义（services / properties / actions 及其 siid/piid/aiid）。
/// 这是「按设备类型设计差异化控制」的数据来源：每个设备的真实可控能力都在 spec 里。
///
/// 数据源：miot-spec.org 公共实例 API（行业标准的 miot 规范库，通常可用）。
/// 缓存：成功结果写入 插件目录/miot_specs/{model}.json，下次免网络；
///       用户也可手动把 &lt;model&gt;.json 放进该目录离线供给。
/// 失败安全：拉取失败时返回 null，调用方回退到基础开关控制，绝不影响其他功能。
/// </summary>
public static class MiotSpecService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(12) };

    static MiotSpecService()
    {
        // 部分 CDN / 反爬需要浏览器 UA
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
    }

    private static string CacheDir
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

    // 内存缓存（含 null，避免会话内反复打网络）；成功的 spec 额外落盘
    private static readonly Dictionary<string, MiotSpec?> MemCache = new();

    private static string[] Sources(string model) =>
        new[]
        {
            $"https://miot-spec.org/miot-spec-v2/instance?type={Uri.EscapeDataString(model)}",
            $"https://raw.githubusercontent.com/Maxmudov/miot-specs/master/{Uri.EscapeDataString(model)}.json"
        };

    public static async Task<MiotSpec?> GetSpecAsync(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (MemCache.TryGetValue(model, out var cached)) return cached;

        var file = Path.Combine(CacheDir, Sanitize(model) + ".json");
        if (File.Exists(file))
        {
            try
            {
                var spec = Parse(File.ReadAllText(file));
                MemCache[model] = spec;
                return spec;
            }
            catch
            {
                // 文件损坏则忽略，继续走网络
            }
        }

        foreach (var url in Sources(model))
        {
            try
            {
                var json = await HttpClient.GetStringAsync(url);
                if (string.IsNullOrWhiteSpace(json) ||
                    json.Contains("data not found", StringComparison.OrdinalIgnoreCase) ||
                    json.Length < 20)
                    continue;

                var spec = Parse(json);
                if (spec != null && spec.Services.Count > 0)
                {
                    try { File.WriteAllText(file, json); } catch { /* 缓存失败不致命 */ }
                    MemCache[model] = spec;
                    return spec;
                }
            }
            catch
            {
                // 该源失败，尝试下一个
            }
        }

        MemCache[model] = null; // 本会话内不再重试，避免刷屏打网络
        return null;
    }

    private static MiotSpec? Parse(string json)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<MiotSpec>(json);
            if (spec == null || spec.Services.Count == 0) return null;
            // 清理 / 规整
            foreach (var svc in spec.Services)
            {
                svc.Properties ??= new();
                svc.Actions ??= new();
            }
            return spec;
        }
        catch
        {
            return null;
        }
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
