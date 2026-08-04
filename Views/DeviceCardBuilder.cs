using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MiIsland.Models;

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
        Action<string, double>? onBrightness = null)
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
            var dot = new Border
            {
                Width = 10, Height = 10,
                CornerRadius = new CornerRadius(5),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Background = Brush.Parse(status.StatusColor)
            };
            Grid.SetRowSpan(dot, 2);
            top.Children.Add(dot);
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
            Text = status.Kind == MiDeviceKind.Sensor ? status.SensorText : status.StatusText,
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
        // Sensor: 只读展示, 无控件

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
