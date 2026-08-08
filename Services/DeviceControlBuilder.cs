using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MiIsland.Models;

namespace MiIsland.Services;

/// <summary>
/// 把设备的 miot-spec 转换成一组「设备控制项」（DeviceControlItem），供 UI 渲染为
/// 开关 / 滑块 / 下拉 / 按钮 / 文本输入等差异化控件。匹配基于标准 miot 属性/动作短名，
/// 因此对任意型号通用；未知 URN 按 format/access/value-list 给通用控件兜底。
/// </summary>
public static class DeviceControlBuilder
{
    // 服务短名 → 中文分组（用于排版归类）
    private static readonly Dictionary<string, string> ServiceGroup = new()
    {
        ["switch"] = "电源",
        ["light"] = "灯光",
        ["fan"] = "风扇",
        ["air-purifier"] = "空气净化",
        ["air-conditioner"] = "空调",
        ["air-condition-outlet"] = "空调",
        ["humidifier"] = "加湿",
        ["vacuum"] = "扫地机器人",
        ["robot-cleaner"] = "扫地机器人",
        ["washer"] = "洗衣",
        ["fridge"] = "冰箱",
        ["heater"] = "取暖",
        ["kettle"] = "热水壶",
        ["player"] = "音箱",
        ["television"] = "电视",
        ["camera"] = "摄像头",
        ["ptz"] = "云台",
        ["physical-controls"] = "物理控制",
        ["indicator-light"] = "指示灯",
        ["child-lock"] = "安全",
    };

    /// <summary>生成设备控制项。spec 为 null 时返回空列表（调用方回退基础控制）。</summary>
    public static List<DeviceControlItem> Build(MiCloudDevice device, MiotSpec? spec)
    {
        var items = new List<DeviceControlItem>();
        if (spec == null) return items;

        // 窗帘由窗口的专用 UI 处理（legacy set_curtain），不在此生成
        if (device.Kind == MiDeviceKind.Curtain) return items;

        foreach (var svc in spec.Services)
        {
            var svcShort = MiotSpecService.ServiceShortName(svc.Type);
            if (svcShort is "device-information" or "device") continue; // 跳过固件/序列号等元数据

            var group = ServiceGroup.TryGetValue(svcShort, out var g) ? g : null;

            // === 属性 ===
            foreach (var prop in svc.Properties)
            {
                if (prop.Access.Count == 0) prop.Access = new List<string> { "read" };
                var writable = prop.Access.Contains("write");
                var shortName = MiotLabels.ShortName(prop.Type);

                (string Label, ControlItemType Type, string? Unit) info;
                var known = MiotLabels.TryGetProp(shortName, out info);
                if (!known) info = MiotLabels.FallbackProp(prop, shortName);

                // 已知控件但属性只读 → 降级为只读展示
                var type = info.Type;
                if (!writable && type is ControlItemType.Toggle or ControlItemType.Slider or ControlItemType.Dropdown)
                    type = ControlItemType.ReadOnly;

                var item = new DeviceControlItem
                {
                    Id = $"{svc.Siid}.{prop.Piid}",
                    Label = info.Label,
                    Group = group,
                    Type = type,
                    Siid = svc.Siid,
                    Piid = prop.Piid,
                    Unit = info.Unit ?? prop.Unit,
                    Hint = prop.Description
                };

                // 取值范围
                if (prop.ValueRange != null && prop.ValueRange.Count >= 2)
                {
                    if (prop.ValueRange[0].ValueKind == JsonValueKind.Number)
                        item.Min = prop.ValueRange[0].GetDouble();
                    if (prop.ValueRange[1].ValueKind == JsonValueKind.Number)
                        item.Max = prop.ValueRange[1].GetDouble();
                    if (prop.ValueRange.Count >= 3 && prop.ValueRange[2].ValueKind == JsonValueKind.Number)
                        item.Step = prop.ValueRange[2].GetDouble();
                    if (item.Min == null) item.Min = 0;
                    if (item.Max == null) item.Max = 100;
                }

                // 下拉选项
                if (prop.ValueList != null)
                {
                    foreach (var o in prop.ValueList)
                        item.Options.Add((o.Name, JsonElementToObject(o.Value)));
                }

                items.Add(item);
            }

            // === 动作 ===
            foreach (var act in svc.Actions)
            {
                var shortName = MiotLabels.ShortName(act.Type);
                (string Label, string? Group) ainfo;
                var known = MiotLabels.TryGetAction(shortName, out ainfo);
                if (!known) ainfo = MiotLabels.FallbackAction(act, shortName);

                var type = (shortName is "play-text" or "tts" or "say")
                    ? ControlItemType.TextInput
                    : ControlItemType.Button;

                items.Add(new DeviceControlItem
                {
                    Id = $"{svc.Siid}.a{act.Aiid}",
                    Label = ainfo.Label,
                    Group = ainfo.Group ?? group,
                    Type = type,
                    Siid = svc.Siid,
                    Aiid = act.Aiid,
                    ActionIn = act.In.Select(a => (object)a.Name).ToList(),
                    Hint = act.Description
                });
            }
        }

        // 把主「开关(on)」排到最前，作为设备主电源
        items.Sort((a, b) =>
        {
            var aOn = a.Type == ControlItemType.Toggle && a.Label == "开关" ? 0 : 1;
            var bOn = b.Type == ControlItemType.Toggle && b.Label == "开关" ? 0 : 1;
            return aOn - bOn;
        });

        return items;
    }

    private static object JsonElementToObject(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.TryGetInt32(out var i) ? i : v.GetDouble(),
        JsonValueKind.String => v.GetString() ?? "",
        _ => v.GetRawText()
    };
}
