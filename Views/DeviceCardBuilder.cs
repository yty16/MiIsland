using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MiIsland.Models;
using MiIsland.Services;

namespace MiIsland.Views;

/// <summary>
/// 设备卡片 UI 构建器。组件 (MiHomeComponent) 与托盘控制窗口 (MiIslandControlWindow)
/// 共用同一套卡片外观，避免逻辑与界面两处维护。
/// </summary>
public static class DeviceCardBuilder
{
    public static Control BuildCard(
        MiDeviceStatus status,
        Action<string>? onToggle = null,
        Action<string, string>? onCurtain = null,
        Action<string, double>? onBrightness = null,
        Action<string>? onOpenDetail = null)
    {
        var card = new Border
        {
            Margin = new Thickness(2),
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(6),
            Background = Brush.Parse("#1A000000")
        };

        var root = new StackPanel { Spacing = 4 };

        // === 第一行: 设备图标/状态点 + 名称 + 状态文字 + 右侧控件 ===
        var top = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto")
        };

        // 左侧：自定义/云端设备图片优先；无图时回退状态点
        var hasImage = !string.IsNullOrEmpty(status.IconPath) && File.Exists(status.IconPath);
        if (hasImage)
        {
            try
            {
                var img = new Image
                {
                    Source = new Bitmap(status.IconPath!),
                    Width = 34, Height = 34,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                    Clip = new RectangleGeometry(new Rect(0, 0, 34, 34), 6, 6)
                };
                Grid.SetRowSpan(img, 2);
                top.Children.Add(img);
            }
            catch
            {
                hasImage = false;
            }
        }
        if (!hasImage)
        {
            // 类型占位图：按设备类型给不同颜色 + emoji 字形，不再回退 MiIsland 图标
            var placeholder = new Border
            {
                Width = 34, Height = 34,
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Background = Brush.Parse(DeviceImageHelper.PlaceholderColor(status.Kind)),
                Child = new TextBlock
                {
                    Text = DeviceImageHelper.PlaceholderGlyph(status.Kind),
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetRowSpan(placeholder, 2);
            top.Children.Add(placeholder);
        }

        var nameText = new TextBlock
        {
            Text = status.Name,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            Foreground = Brush.Parse("#333333")
        };
        Grid.SetRow(nameText, 0);
        Grid.SetColumn(nameText, 1);
        top.Children.Add(nameText);

        var statusText = new TextBlock
        {
            Text = status.Kind is MiDeviceKind.Sensor or MiDeviceKind.Camera or MiDeviceKind.Router
                ? status.SensorText : status.StatusText,
            FontSize = 11,
            Foreground = Brush.Parse("#9E9E9E"),
            Margin = new Thickness(0, 2, 0, 0)
        };
        Grid.SetRow(statusText, 1);
        Grid.SetColumn(statusText, 1);
        top.Children.Add(statusText);

        // 右侧控件按设备类型渲染
        if (status.Kind is MiDeviceKind.Light or MiDeviceKind.Switch or MiDeviceKind.Generic)
        {
            // 灯 / 开关 / 插座 / 通用：直接内联电源开关（这些设备确实以「开关」为主要控制）
            var toggleBtn = new Button
            {
                Content = status.PowerButtonText,
                FontSize = 12,
                Width = 56, Height = 30,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brush.Parse(status.PowerButtonColor),
                Foreground = Brush.Parse("#FFFFFF"),
                CornerRadius = new CornerRadius(4),
                Tag = status.Did
            };
            if (onToggle != null)
                toggleBtn.Click += (_, _) => onToggle(status.Did);
            Grid.SetRowSpan(toggleBtn, 2);
            Grid.SetColumn(toggleBtn, 2);
            top.Children.Add(toggleBtn);
        }
        else if (status.Kind == MiDeviceKind.Curtain)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            foreach (var (label, action) in new[] { ("开", "open"), ("停", "pause"), ("关", "close") })
            {
                var b = new Button
                {
                    Content = label,
                    FontSize = 11,
                    Padding = new Thickness(8, 2),
                    Background = Brush.Parse("#E0E0E0"),
                    Foreground = Brush.Parse("#333333"),
                    CornerRadius = new CornerRadius(3),
                    Tag = $"{status.Did}:{action}"
                };
                if (onCurtain != null)
                    b.Click += (_, _) => onCurtain(status.Did, action);
                panel.Children.Add(b);
            }
            Grid.SetRowSpan(panel, 2);
            Grid.SetColumn(panel, 2);
            top.Children.Add(panel);
        }
        else if (onOpenDetail != null)
        {
            // 其它设备（电视 / 空调 / 扫地机 / 净化器 / 风扇 / 加湿器 / 音箱 / 热水壶 / 取暖器 /
            // 洗衣机 / 冰箱 / 传感器 / 摄像头 / 路由器 / 门锁 / 网关）：不再只给一个「开关」，
            // 而是提供「控制」入口，打开按设备类型设计的详细控制面板（开关 / 模式 / 音量 / 动作等）。
            var ctrlBtn = new Button
            {
                Content = "控制",
                FontSize = 12,
                Width = 56, Height = 30,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brush.Parse("#2196F3"),
                Foreground = Brush.Parse("#FFFFFF"),
                CornerRadius = new CornerRadius(4),
                Tag = status.Did
            };
            ctrlBtn.Click += (_, _) => onOpenDetail(status.Did);
            Grid.SetRowSpan(ctrlBtn, 2);
            Grid.SetColumn(ctrlBtn, 2);
            top.Children.Add(ctrlBtn);
        }
        // 无 onOpenDetail 且非上述类型：只读展示

        root.Children.Add(top);

        // === 灯: 亮度滑块 (先设值再挂事件, 避免初始赋值误触发回查) ===
        if (status.Kind == MiDeviceKind.Light && onBrightness != null)
        {
            var didLocal = status.Did;
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Margin = new Thickness(0, 6, 0, 0),
                Tag = status.Did
            };
            slider.Value = Math.Clamp(status.Brightness ?? 50, 0, 100);
            slider.ValueChanged += (_, _) => onBrightness(didLocal, slider.Value);
            root.Children.Add(slider);
        }

        card.Child = root;
        return card;
    }
}
