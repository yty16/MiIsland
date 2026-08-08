using MiIsland.Models;

namespace MiIsland.Services;

/// <summary>
/// miot 标准属性/动作类型 URN → 友好中文标签 / 控件类型 的映射。
/// 匹配用「短名」（URN 中 property:/action: 与末尾 :hexid 之间的那段），
/// 不依赖具体 hex 编码，因此对所有型号通用。
/// 参考 miot-spec 标准属性/动作定义（urn:miot-spec-v2:property:* / :action:*）。
/// </summary>
public static class MiotLabels
{
    /// <summary>从 URN 提取短名，例如 urn:miot-spec-v2:property:on:00000008 → "on"。</summary>
    public static string ShortName(string urn)
    {
        if (string.IsNullOrEmpty(urn)) return "";
        var idx = urn.LastIndexOf(':');
        if (idx <= 0) return urn;
        var tail = urn[(idx + 1)..];
        // 去掉末尾 :hexid（纯十六进制段）
        if (tail.Length == 8 && IsHex(tail)) urn = urn[..idx];
        idx = urn.LastIndexOf(':');
        if (idx < 0) return urn;
        return urn[(idx + 1)..];
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    // 属性短名 → (标签, 控件类型, 单位)
    private static readonly Dictionary<string, (string Label, ControlItemType Type, string? Unit)> PropMap = new()
    {
        ["on"] = ("开关", ControlItemType.Toggle, null),
        ["brightness"] = ("亮度", ControlItemType.Slider, "%"),
        ["color-temperature"] = ("色温", ControlItemType.Slider, "K"),
        ["mode"] = ("模式", ControlItemType.Dropdown, null),
        ["fan-level"] = ("风量", ControlItemType.Dropdown, null),
        ["volume"] = ("音量", ControlItemType.Slider, "%"),
        ["mute"] = ("静音", ControlItemType.Toggle, null),
        ["temperature"] = ("温度", ControlItemType.ReadOnly, "°C"),
        ["actual-temperature"] = ("室温", ControlItemType.ReadOnly, "°C"),
        ["relative-humidity"] = ("湿度", ControlItemType.ReadOnly, "%"),
        ["target-temperature"] = ("目标温度", ControlItemType.Slider, "°C"),
        ["target-humidity"] = ("目标湿度", ControlItemType.Slider, "%"),
        ["battery-level"] = ("电量", ControlItemType.ReadOnly, "%"),
        ["charging-state"] = ("充电状态", ControlItemType.ReadOnly, null),
        ["status"] = ("状态", ControlItemType.ReadOnly, null),
        ["fault"] = ("故障", ControlItemType.ReadOnly, null),
        ["filter-life-level"] = ("滤芯寿命", ControlItemType.ReadOnly, "%"),
        ["filter-used-time"] = ("滤芯已用", ControlItemType.ReadOnly, null),
        ["motor-control"] = ("窗帘", ControlItemType.Toggle, null),
        ["indicator-light"] = ("指示灯", ControlItemType.Toggle, null),
        ["indicator-light-brightness"] = ("指示灯亮度", ControlItemType.Slider, "%"),
        ["child-lock"] = ("童锁", ControlItemType.Toggle, null),
        ["heat-level"] = ("档位", ControlItemType.Dropdown, null),
        ["speed"] = ("风速", ControlItemType.Dropdown, null),
        ["load-power"] = ("功率", ControlItemType.ReadOnly, "W"),
        ["pm2.5"] = ("PM2.5", ControlItemType.ReadOnly, null),
        ["motor-speed"] = ("转速", ControlItemType.ReadOnly, null),
        ["water-level"] = ("水量", ControlItemType.Dropdown, null),
        ["sweep-type"] = ("清扫类型", ControlItemType.Dropdown, null),
        ["mop-mode"] = ("拖地模式", ControlItemType.Dropdown, null),
        ["clean-area"] = ("清扫区域", ControlItemType.ReadOnly, null),
        ["air-quality"] = ("空气质量", ControlItemType.ReadOnly, null),
        ["co2"] = ("CO₂", ControlItemType.ReadOnly, null),
        ["tvoc"] = ("TVOC", ControlItemType.ReadOnly, null),
        ["illumination"] = ("光照", ControlItemType.ReadOnly, null),
        ["no-cloud"] = ("无云模式", ControlItemType.Toggle, null),
        ["motion-detection"] = ("移动侦测", ControlItemType.Toggle, null),
        ["power-supply"] = ("电源", ControlItemType.Toggle, null),
    };

    // 动作短名 → (标签, 分组)
    private static readonly Dictionary<string, (string Label, string? Group)> ActionMap = new()
    {
        // 扫地机器人
        ["start-sweep"] = ("开始清扫", "清扫控制"),
        ["start-sweeping"] = ("开始清扫", "清扫控制"),
        ["stop-sweeping"] = ("停止清扫", "清扫控制"),
        ["pause-sweeping"] = ("暂停清扫", "清扫控制"),
        ["resume-sweeping"] = ("继续清扫", "清扫控制"),
        ["go-charging"] = ("回充", "清扫控制"),
        ["dock"] = ("回充", "清扫控制"),
        ["charge"] = ("回充", "清扫控制"),
        ["find-robot"] = ("查找机器人", "清扫控制"),
        ["self-cleaning"] = ("自动清洗", "清扫控制"),
        // 音箱 / 播放器
        ["play"] = ("播放", "播放控制"),
        ["pause"] = ("暂停", "播放控制"),
        ["stop"] = ("停止", "播放控制"),
        ["next"] = ("下一首", "播放控制"),
        ["previous"] = ("上一首", "播放控制"),
        ["play-text"] = ("文字播报", "播报"),
        ["tts"] = ("文字播报", "播报"),
        ["say"] = ("文字播报", "播报"),
        // 摄像头云台
        ["move"] = ("云台旋转", "云台"),
        ["ptz-move"] = ("云台旋转", "云台"),
        // 通用
        ["identify"] = ("指示灯闪烁", null),
    };

    public static bool TryGetProp(string shortName, out (string Label, ControlItemType Type, string? Unit) info)
        => PropMap.TryGetValue(shortName, out info);

    public static bool TryGetAction(string shortName, out (string Label, string? Group) info)
        => ActionMap.TryGetValue(shortName, out info);

    /// <summary>未知属性 URN 时，按 format / access / value-list 给一个通用控件与标签兜底。</summary>
    public static (string Label, ControlItemType Type, string? Unit) FallbackProp(MiotProperty p, string shortName)
    {
        var label = !string.IsNullOrEmpty(p.Description) ? p.Description : shortName;
        var writable = p.Access.Contains("write");
        if (p.ValueList != null && p.ValueList.Count > 0)
            return (label, writable ? ControlItemType.Dropdown : ControlItemType.ReadOnly, null);
        if (p.Format is "bool")
            return (label, writable ? ControlItemType.Toggle : ControlItemType.ReadOnly, null);
        if (p.Format is "uint8" or "int8" or "uint16" or "int16" or "uint32" or "int32" or "float")
            return (label, writable ? ControlItemType.Slider : ControlItemType.ReadOnly, null);
        return (label, ControlItemType.ReadOnly, null);
    }

    public static (string Label, string? Group) FallbackAction(MiotAction a, string shortName)
        => (!string.IsNullOrEmpty(a.Description) ? a.Description : shortName, null);
}
