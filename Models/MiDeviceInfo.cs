using System.Text.Json.Serialization;

namespace MiIsland.Models;

/// <summary>
/// 小米账号登录凭证 (持久化)
/// </summary>
public class MiAccountInfo
{
    /// <summary>小米账号 (手机号/邮箱/Xiaomi ID)</summary>
    public string Username { get; set; } = "";

    /// <summary>账号密码</summary>
    public string Password { get; set; } = "";

    /// <summary>国家/地区代码 (默认 "cn" 中国大陆)</summary>
    public string Country { get; set; } = "cn";
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
/// 设备实时状态 (UI绑定)
/// </summary>
public class MiDeviceStatus
{
    public string Name { get; set; } = "";
    public string Did { get; set; } = "";
    public bool IsOnline { get; set; }
    public bool IsPoweredOn { get; set; }
    public int? Brightness { get; set; }
    public double? Temperature { get; set; }
    public double? Humidity { get; set; }
    public string? LastError { get; set; }
    public DateTime LastUpdated { get; set; }

    public string StatusText => IsOnline
        ? (IsPoweredOn ? "已开启" : "已关闭")
        : "离线";

    public string StatusColor => IsOnline
        ? (IsPoweredOn ? "#4CAF50" : "#9E9E9E")
        : "#F44336";
}
