using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiIsland.Models;

/// <summary>
/// 控件类型（由 miot 属性/动作的 format / access / value-list 推导，或按标准 URN 友好映射）。
/// </summary>
public enum ControlItemType
{
    /// <summary>开关（bool 属性或可触发动作）</summary>
    Toggle,
    /// <summary>数值滑块（有 value-range 的数值属性）</summary>
    Slider,
    /// <summary>下拉选择（有 value-list 的属性）</summary>
    Dropdown,
    /// <summary>按钮（动作 / 无入参动作）</summary>
    Button,
    /// <summary>文本输入 + 按钮（如音箱文字播报）</summary>
    TextInput,
    /// <summary>只读展示</summary>
    ReadOnly
}

/// <summary>
/// 单个设备控制项（由 DeviceControlBuilder 从 miot spec 生成，供 UI 渲染）。
/// 属性类控件带 Siid/Piid；动作类控件带 Siid/Aiid。
/// </summary>
public class DeviceControlItem
{
    /// <summary>稳定标识（按 siid/piid 或 siid/aiid 生成）</summary>
    public string Id { get; set; } = "";

    /// <summary>中文标签</summary>
    public string Label { get; set; } = "";

    /// <summary>分组名（如「清扫控制」「音量与播放」），用于排版归类</summary>
    public string? Group { get; set; }

    public ControlItemType Type { get; set; }

    public int Siid { get; set; }
    public int Piid { get; set; }
    public int Aiid { get; set; }

    /// <summary>当前值（渲染时回填）；bool / double / string / int</summary>
    public object? CurrentValue { get; set; }

    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public string? Unit { get; set; }

    /// <summary>下拉选项（Label, Value）</summary>
    public List<(string Label, object Value)> Options { get; set; } = new();

    /// <summary>动作入参（如文字播报的文本；通常为空）</summary>
    public List<object>? ActionIn { get; set; }

    /// <summary>原始描述（未知 URN 时作为标签兜底）</summary>
    public string? Hint { get; set; }

    public bool IsAction => Type == ControlItemType.Button || Type == ControlItemType.TextInput;
}

// ============ miot-spec 实例数据模型 ============

public class MiotSpec
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("services")]
    public List<MiotService> Services { get; set; } = new();
}

public class MiotService
{
    [JsonPropertyName("iid")]
    public int Siid { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("properties")]
    public List<MiotProperty> Properties { get; set; } = new();

    [JsonPropertyName("actions")]
    public List<MiotAction> Actions { get; set; } = new();
}

public class MiotProperty
{
    [JsonPropertyName("iid")]
    public int Piid { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("access")]
    public List<string> Access { get; set; } = new();

    [JsonPropertyName("format")]
    public string Format { get; set; } = "";

    [JsonPropertyName("value-range")]
    public List<JsonElement>? ValueRange { get; set; }

    [JsonPropertyName("value-list")]
    public List<MiotValueListItem>? ValueList { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }
}

public class MiotValueListItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }
}

public class MiotAction
{
    [JsonPropertyName("iid")]
    public int Aiid { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("in")]
    public List<MiotArg> In { get; set; } = new();

    [JsonPropertyName("out")]
    public List<MiotArg> Out { get; set; } = new();
}

public class MiotArg
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}
