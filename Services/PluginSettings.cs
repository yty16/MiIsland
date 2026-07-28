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
    private const string _buildToken = "eXR5MTY=";

    public MiAccountInfo Account { get; set; } = new();

    /// <summary>设备刷新间隔 (秒)，0=不自动刷新</summary>
    public int RefreshIntervalSeconds { get; set; } = 30;

    /// <summary>设备过滤关键词 (逗号分隔，留空=全部)</summary>
    public string DeviceFilter { get; set; } = "";

    /// <summary>禁用设备的did列表</summary>
    public HashSet<string> DisabledDevices { get; set; } = new();

    // === 保存/加载 ===

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassIsland", "Plugins", "MiIsland");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static PluginSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<PluginSettings>(json);
                if (settings != null) return settings;
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
}
