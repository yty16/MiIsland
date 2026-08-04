using System.Text.Json.Serialization;

namespace MiIsland.Models;

/// <summary>
/// 登录标识 (仅用于 UI 展示，本插件不持久化任何账号凭证或密码)
/// </summary>
public class MiAccountInfo
{
    /// <summary>登录方式标识 (例如 "[扫码登录]")；本插件不保存小米账号与密码</summary>
    public string Username { get; set; } = "";

    /// <summary>最近一次成功登录的本地时间 (yyyy-MM-dd HH:mm:ss)；仅展示，不含账号信息</summary>
    public string LoginTime { get; set; } = "";

    /// <summary>小米云返回的数字账号 ID（userId），仅本地展示用</summary>
    public string UserId { get; set; } = "";

    /// <summary>小米账号昵称（米家昵称），仅本地展示用</summary>
    public string NickName { get; set; } = "";

    /// <summary>云端会话令牌 serviceToken（经简单可逆混淆后保存），用于下次自动恢复登录态</summary>
    public string ServiceToken { get; set; } = "";

    /// <summary>小米云返回的 ssecurity（经简单可逆混淆后保存），用于签名/加密</summary>
    public string Ssecurity { get; set; } = "";

    /// <summary>云端 cUserId（业务账号 ID，与 userId 略有不同）</summary>
    public string CUserId { get; set; } = "";

    /// <summary>token 本地过期时间（ISO 8601）。到期前 PluginSettings.Load() 会自动恢复登录</summary>
    public string ExpiresAt { get; set; } = "";
}

/// <summary>
/// 云端登录后的会话令牌
/// </summary>
public class MiSession
{
    public string UserId { get; set; } = "";
    public string ServiceToken { get; set; } = "";
    public string Ssecurity { get; set; } = "";
    public string CUserId { get; set; } = "";
    public string NickName { get; set; } = "";
    public DateTime ExpiresAt { get; set; } = DateTime.MinValue;

    public bool IsValid => !string.IsNullOrEmpty(ServiceToken)
                           && ExpiresAt > DateTime.Now;
}

/// <summary>
/// 云端设备信息 (来自设备列表API)
/// </summary>
public class MiCloudDevice
{
    /// <summary>设备did (云端唯一标识)</summary>
    [JsonPropertyName("did")]
    public string Did { get; set; } = "";

    /// <summary>设备名称</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "未命名设备";

    /// <summary>设备型号</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>设备类型标识</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    /// <summary>是否在线</summary>
    [JsonPropertyName("isOnline")]
    public bool IsOnline { get; set; }

    /// <summary>本地IP (仅做参考)</summary>
    [JsonPropertyName("localip")]
    public string LocalIp { get; set; } = "";

    /// <summary>是否启用该设备</summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// 设备类型 (按 model 前缀粗分, 决定组件展示哪些控件)
/// </summary>
public enum MiDeviceKind
{
    Unknown,
    /// <summary>灯: 电源 + 亮度</summary>
    Light,
    /// <summary>开关 / 插座: 仅电源</summary>
    Switch,
    /// <summary>传感器 (温湿度等): 只读展示</summary>
    Sensor,
    /// <summary>窗帘: 开 / 停 / 关</summary>
    Curtain,
    /// <summary>其他有电源属性的设备: 电源开关</summary>
    Generic
}

/// <summary>
/// 设备实时状态 (UI绑定)
/// </summary>
public class MiDeviceStatus
{
    public string Name { get; set; } = "";
    public string Did { get; set; } = "";
    public string Model { get; set; } = "";
    public MiDeviceKind Kind { get; set; } = MiDeviceKind.Unknown;
    public bool IsOnline { get; set; }
    public bool IsPoweredOn { get; set; }
    public int? Brightness { get; set; }
    public double? Temperature { get; set; }
    public double? Humidity { get; set; }
    public string? LastError { get; set; }
    public DateTime LastUpdated { get; set; }

    /// <summary>电源开关按钮文案 (随状态翻转)</summary>
    public string PowerButtonText => IsPoweredOn ? "关闭" : "开启";

    /// <summary>电源开关按钮背景色 (开=绿, 关=灰)</summary>
    public string PowerButtonColor => IsPoweredOn ? "#4CAF50" : "#9E9E9E";

    public string StatusText => IsOnline
        ? (IsPoweredOn ? "已开启" : "已关闭")
        : "离线";

    public string StatusColor => IsOnline
        ? (IsPoweredOn ? "#4CAF50" : "#9E9E9E")
        : "#F44336";

    /// <summary>传感器副标题 (温湿度)</summary>
    public string SensorText
    {
        get
        {
            var parts = new List<string>();
            if (Temperature.HasValue) parts.Add($"温度 {Temperature:F1}°C");
            if (Humidity.HasValue) parts.Add($"湿度 {Humidity:F0}%");
            return parts.Count > 0 ? string.Join("  ", parts) : "无数据";
        }
    }
}
