using System.IO;
using System.Text.Json;
using MiIsland.Models;

namespace MiIsland.Services;

/// <summary>
/// 插件设置持久化 (JSON文件)
/// </summary>
public class PluginSettings
{
    // 内部构建标识（用于版本溯源，非凭证、不对外暴露）
    private static readonly string _buildToken = "eXR5MTY=";

    // 供内部溯源读取（不写入任何持久化文件）
    internal static string BuildSignature => _buildToken;

    public MiAccountInfo Account { get; set; } = new();

    /// <summary>设备刷新间隔 (秒)，0=不自动刷新</summary>
    public int RefreshIntervalSeconds { get; set; } = 30;

    /// <summary>设备过滤关键词 (逗号分隔，留空=全部)</summary>
    public string DeviceFilter { get; set; } = "";

    /// <summary>禁用设备的did列表</summary>
    public HashSet<string> DisabledDevices { get; set; } = new();

    /// <summary>每个设备的自定义图标（did -> 本地图片绝对路径），覆盖云端自动图标</summary>
    public Dictionary<string, string> DeviceIcons { get; set; } = new();

    // === 保存/加载 ===

    // 存插件目录 (DLL 同目录)，卸载插件时整个目录被删 → 登录状态一并清除，重装后不再自动登录。
    // 不再写入 AppData，避免卸载插件后凭证残留在系统目录里。
    private static readonly string SettingsDir = Path.GetDirectoryName(
        System.Reflection.Assembly.GetExecutingAssembly().Location)
        ?? AppContext.BaseDirectory;

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static PluginSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<PluginSettings>(json);
                if (settings != null)
                {
                    // 自动恢复登录 session(如果尚未过期)
                    if (!string.IsNullOrEmpty(settings.Account.ServiceToken))
                    {
                        MiCloudService.Instance.TryRestoreSession(
                            settings.Account.ServiceToken,
                            settings.Account.Ssecurity,
                            settings.Account.UserId,
                            settings.Account.CUserId,
                            settings.Account.ExpiresAt,
                            settings.Account.NickName);
                    }
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiHome] Settings load error: {ex.Message}");
        }
        return new PluginSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiHome] Settings save error: {ex.Message}");
        }
    }

    /// <summary>判断设备是否启用</summary>
    public bool IsDeviceEnabled(string did)
    {
        return !DisabledDevices.Contains(did);
    }

    /// <summary>切换设备启用状态</summary>
    public void ToggleDevice(string did)
    {
        if (DisabledDevices.Contains(did))
            DisabledDevices.Remove(did);
        else
            DisabledDevices.Add(did);
    }

    /// <summary>获取某设备的自定义图标本地路径（不存在或文件已失效则返回 null）</summary>
    public string? GetDeviceIcon(string did)
        => DeviceIcons.TryGetValue(did, out var p) && File.Exists(p) ? p : null;

    /// <summary>设置某设备的自定义图标（覆盖云端自动图标）；不自动保存，调用方按需 Save()</summary>
    public void SetDeviceIcon(string did, string path) => DeviceIcons[did] = path;

    /// <summary>清除某设备的自定义图标；不自动保存，调用方按需 Save()</summary>
    public void ClearDeviceIcon(string did) => DeviceIcons.Remove(did);
}
