using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MiIsland.Models;
using MiIsland.Services;
using Timer = System.Timers.Timer;

namespace MiIsland.Views;

/// <summary>
/// 单个设备控制窗口。由桌面快捷方式 classisland://plugins/MiIsland/device/{did} 打开。
/// 展示该设备按 miot-spec 生成的「差异化控制面板」：开关 / 滑块 / 下拉 / 动作按钮 / 文字播报等，
/// 操作后回查真实状态。每个 did 对应一个复用窗口实例（按 did 缓存）。
/// 若设备规范（spec）暂时无法拉取，回退到基础电源开关，不影响使用。
/// </summary>
public class MiIslandDeviceWindow : Window
{
    private readonly MiCloudService _cloudService = MiCloudService.Instance;
    private readonly PluginSettings _settings = PluginSettings.Load();
    private readonly string _did;

    private MiCloudDevice? _device;
    private MiDeviceStatus? _status;
    private MiotSpec? _spec;
    private List<DeviceControlItem> _controlItems = new();

    private Timer? _refreshTimer;
    private Timer? _brightnessTimer;
    private int? _pendingBrightness;
    private Timer? _sliderTimer;
    private (int Siid, int Piid, double Value)? _pendingSlider;

    private StackPanel _cardHost = null!;
    private TextBlock _statusText = null!;
    private Button _refreshButton = null!;
    private Image _iconImage = null!;
    private Border _iconPlaceholder = null!;

    private static readonly Dictionary<string, MiIslandDeviceWindow> Instances = new();

    public MiIslandDeviceWindow(string did)
    {
        _did = did;
        Title = "MiIsland 设备";
        Width = 380;
        Height = 460;
        MinWidth = 320;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUI();
        _ = LoadAsync();
    }

    /// <summary>按 did 显示/激活对应设备窗口（已存在则激活，不重复创建）。</summary>
    public static void ShowDeviceWindow(string did)
    {
        if (string.IsNullOrEmpty(did)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (Instances.TryGetValue(did, out var existing))
            {
                existing.Show();
                existing.Activate();
                return;
            }

            var w = new MiIslandDeviceWindow(did);
            w.Closed += (_, _) => { Instances.Remove(did); };
            Instances[did] = w;
            w.Show();
            w.Activate();
        });
    }

    private void BuildUI()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(12)
        };

        // 标题栏
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        _iconImage = new Image
        {
            Width = 30, Height = 30,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsVisible = false
        };
        Grid.SetColumn(_iconImage, 0);
        header.Children.Add(_iconImage);

        _iconPlaceholder = new Border
        {
            Width = 30, Height = 30,
            CornerRadius = new CornerRadius(7),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsVisible = false
        };
        Grid.SetColumn(_iconPlaceholder, 0);
        header.Children.Add(_iconPlaceholder);
        header.Children.Add(new TextBlock
        {
            Text = "MiIsland 单设备控制",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#333333"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(header.Children[header.Children.Count - 1], 1);
        _refreshButton = new Button
        {
            Content = "刷新",
            Padding = new Thickness(12, 4),
            Background = Brush.Parse("#E0E0E0"),
            Foreground = Brush.Parse("#333333"),
            CornerRadius = new CornerRadius(4)
        };
        _refreshButton.Click += OnRefreshClick;
        Grid.SetColumn(_refreshButton, 2);
        header.Children.Add(_refreshButton);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // 控制面板容器
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 2, 0, 2)
        };
        _cardHost = new StackPanel { Spacing = 8 };
        scroll.Content = _cardHost;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // 状态栏
        _statusText = new TextBlock
        {
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 20,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brush.Parse("#9E9E9E")
        };
        Grid.SetRow(_statusText, 2);
        root.Children.Add(_statusText);

        Content = root;
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        _refreshButton.IsEnabled = false;
        try
        {
            await RefreshStatusAsync();
            await LoadControlsAsync();
        }
        finally
        {
            _refreshButton.IsEnabled = true;
        }
    }

    private async Task LoadAsync()
    {
        if (!_cloudService.IsLoggedIn)
        {
            SetStatus("未登录，请在 ClassIsland 插件设置中登录小米账号", "#FF9800");
            return;
        }

        var (devices, err) = await _cloudService.GetDeviceListAsync();
        if (devices == null)
        {
            SetStatus(string.IsNullOrEmpty(err) ? "获取设备列表失败" : $"获取设备列表失败：{err}", "#F44336");
            return;
        }

        _device = devices.FirstOrDefault(d => d.Did == _did);
        if (_device == null)
        {
            SetStatus("未找到该设备（可能已移出账号）", "#F44336");
            return;
        }

        Title = $"MiIsland · {_device.Name}";

        await LoadDeviceIconAsync();

        if (_settings.RefreshIntervalSeconds > 0)
        {
            _refreshTimer = new Timer(_settings.RefreshIntervalSeconds * 1000) { AutoReset = true };
            _refreshTimer.Elapsed += async (_, _) => { await RefreshStatusAsync(); await LoadControlsAsync(); };
        }

        await RefreshStatusAsync();
        await LoadControlsAsync();
    }

    private async Task LoadDeviceIconAsync()
    {
        if (_device == null) return;
        var path = _settings.GetDeviceIcon(_did)
                ?? await DeviceImageHelper.GetCloudImagePathAsync(_device);
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var p = path;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _iconImage.Source = new Bitmap(p);
                    _iconImage.IsVisible = true;
                    _iconPlaceholder.IsVisible = false;
                });
                return;
            }
            catch
            {
                // 落到占位图
            }
        }

        try
        {
            var kind = _device.Kind;
            var color = DeviceImageHelper.PlaceholderColor(kind);
            var glyph = DeviceImageHelper.PlaceholderGlyph(kind);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _iconImage.IsVisible = false;
                _iconPlaceholder.Background = Brush.Parse(color);
                _iconPlaceholder.Child = new TextBlock
                {
                    Text = glyph,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _iconPlaceholder.IsVisible = true;
            });
        }
        catch
        {
            // 占位图也失败则留空
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_device == null) return;
        try
        {
            var st = await _cloudService.GetDeviceStatusAsync(_device);
            if (st == null)
            {
                SetStatus("获取设备状态失败，请检查网络", "#F44336");
                return;
            }
            _status = st;
            SetStatus($"更新于 {DateTime.Now:HH:mm:ss}", "#4CAF50");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiIsland] device window refresh: {ex.Message}");
        }
    }

    // === 控制面板（按 miot-spec 生成）===

    private async Task LoadControlsAsync()
    {
        if (_device == null) return;
        if (!_device.IsOnline)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _cardHost.Children.Clear();
                _cardHost.Children.Add(new TextBlock
                {
                    Text = "设备当前离线，无法读取/控制。",
                    FontSize = 13,
                    Foreground = Brush.Parse("#F44336"),
                    Margin = new Thickness(0, 4, 0, 0)
                });
            });
            return;
        }

        try
        {
            _spec = await _cloudService.GetSpecAsync(_device.Model);
            _controlItems = DeviceControlBuilder.Build(_device, _spec);

            // 读取所有属性项的当前值，回填到控件
            var propItems = _controlItems.Where(i => !i.IsAction).ToList();
            if (propItems.Count > 0)
            {
                var props = propItems.Select(i => (i.Siid, i.Piid)).ToList();
                var values = await _cloudService.GetMiotPropertiesAsync(_device.Did, props);
                foreach (var it in propItems)
                    if (values.TryGetValue((it.Siid, it.Piid), out var v))
                        it.CurrentValue = v;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiIsland] load controls: {ex.Message}");
        }

        await Dispatcher.UIThread.InvokeAsync(RenderPanel);
    }

    private void RenderPanel()
    {
        _cardHost.Children.Clear();

        if (_controlItems.Count > 0)
        {
            // 按分组渲染
            string? lastGroup = null;
            StackPanel? groupPanel = null;
            foreach (var item in _controlItems)
            {
                if (item.Group != lastGroup)
                {
                    if (!string.IsNullOrEmpty(item.Group))
                    {
                        _cardHost.Children.Add(new TextBlock
                        {
                            Text = item.Group,
                            FontSize = 12,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = Brush.Parse("#607D8B"),
                            Margin = new Thickness(0, 6, 0, 2)
                        });
                    }
                    groupPanel = new StackPanel { Spacing = 6 };
                    _cardHost.Children.Add(groupPanel);
                    lastGroup = item.Group;
                }
                groupPanel?.Children.Add(BuildItemControl(item));
            }
            return;
        }

        // 无 spec：回退基础电源开关（仅灯/开关/插座/通用）
        if (_status != null)
        {
            _cardHost.Children.Add(DeviceCardBuilder.BuildCard(
                _status,
                onToggle: did => _ = ToggleAsync(did),
                onCurtain: (did, a) => _ = CurtainAsync(did, a),
                onBrightness: (did, v) => ScheduleBrightnessSet(did, v),
                onOpenDetail: null));
        }

        _cardHost.Children.Add(new TextBlock
        {
            Text = _spec == null
                ? "未能获取该设备的详细控制能力（需联网拉取 miot 设备规范，首次使用需联网，之后会缓存）。已回退为基础电源开关。"
                : "该设备暂无可写控制项。",
            FontSize = 12,
            Foreground = Brush.Parse("#9E9E9E"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
    }

    private Control BuildItemControl(DeviceControlItem item)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinHeight = 30,
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = item.Label,
            FontSize = 13,
            Foreground = Brush.Parse("#333333"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        switch (item.Type)
        {
            case ControlItemType.Toggle:
                var tog = new ToggleSwitch
                {
                    IsChecked = IsTrue(item.CurrentValue),
                    VerticalAlignment = VerticalAlignment.Center,
                    OffContent = "关", OnContent = "开"
                };
                tog.IsCheckedChanged += (_, _) =>
                    _ = ApplyPropertyAsync(item, tog.IsChecked == true);
                Grid.SetColumn(tog, 1);
                row.Children.Add(tog);
                break;

            case ControlItemType.Slider:
                var min = item.Min ?? 0;
                var max = item.Max ?? 100;
                var slider = new Slider
                {
                    Minimum = min, Maximum = max,
                    Value = Math.Clamp(ToDouble(item.CurrentValue), min, max),
                    Width = 150, VerticalAlignment = VerticalAlignment.Center
                };
                var valText = new TextBlock
                {
                    Text = FormatValue(item.CurrentValue, item.Unit),
                    FontSize = 12, Foreground = Brush.Parse("#757575"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0), MinWidth = 40
                };
                var sliderWrap = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { slider, valText }
                };
                slider.ValueChanged += (_, _) =>
                {
                    valText.Text = $"{(int)Math.Round(slider.Value)}{item.Unit ?? ""}";
                    ScheduleSliderSet(item, slider.Value);
                };
                Grid.SetColumn(sliderWrap, 1);
                row.Children.Add(sliderWrap);
                break;

            case ControlItemType.Dropdown:
                var combo = new ComboBox
                {
                    Width = 150, VerticalAlignment = VerticalAlignment.Center,
                    PlaceholderText = "请选择"
                };
                foreach (var opt in item.Options)
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = opt.Label,
                        Tag = opt.Value
                    });
                // 选中当前值
                for (var i = 0; i < item.Options.Count; i++)
                    if (ValuesEqual(item.Options[i].Value, item.CurrentValue))
                        combo.SelectedIndex = i;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem sel)
                        _ = ApplyPropertyAsync(item, sel.Tag);
                };
                Grid.SetColumn(combo, 1);
                row.Children.Add(combo);
                break;

            case ControlItemType.Button:
                var btn = new Button
                {
                    Content = item.Label,
                    Padding = new Thickness(14, 6),
                    Background = Brush.Parse("#2196F3"),
                    Foreground = Brush.Parse("#FFFFFF"),
                    CornerRadius = new CornerRadius(4)
                };
                btn.Click += async (_, _) =>
                {
                    btn.IsEnabled = false;
                    try { await ApplyActionAsync(item, null); }
                    finally { btn.IsEnabled = true; }
                };
                Grid.SetColumn(btn, 1);
                row.Children.Add(btn);
                break;

            case ControlItemType.TextInput:
                var tb = new TextBox
                {
                    Width = 150, VerticalAlignment = VerticalAlignment.Center,
                    Watermark = "输入要播报的文字"
                };
                var send = new Button
                {
                    Content = item.Label,
                    Padding = new Thickness(10, 6),
                    Margin = new Thickness(6, 0, 0, 0),
                    Background = Brush.Parse("#2196F3"),
                    Foreground = Brush.Parse("#FFFFFF"),
                    CornerRadius = new CornerRadius(4)
                };
                send.Click += async (_, _) =>
                {
                    if (string.IsNullOrWhiteSpace(tb.Text)) return;
                    send.IsEnabled = false;
                    try { await ApplyActionAsync(item, tb.Text.Trim()); }
                    finally { send.IsEnabled = true; }
                };
                var wrap = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { tb, send }
                };
                Grid.SetColumn(wrap, 1);
                row.Children.Add(wrap);
                break;

            default: // ReadOnly
                var ro = new TextBlock
                {
                    Text = FormatValue(item.CurrentValue, item.Unit),
                    FontSize = 13, Foreground = Brush.Parse("#757575"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(ro, 1);
                row.Children.Add(ro);
                break;
        }

        return row;
    }

    // === 应用控制（操作后回查真实状态）===

    private async Task ApplyPropertyAsync(DeviceControlItem item, object? value)
    {
        if (_device == null) return;
        var ok = await _cloudService.SetMiotPropertyAsync(_device.Did, item.Siid, item.Piid, value!);
        if (ok)
        {
            await LoadControlsAsync();
            SetStatus($"已设置「{item.Label}」", "#4CAF50");
        }
        else
        {
            SetStatus($"「{item.Label}」控制失败，请检查网络或设备", "#F44336");
            await LoadControlsAsync(); // 还原显示
        }
    }

    private async Task ApplyActionAsync(DeviceControlItem item, string? text)
    {
        if (_device == null) return;
        object[]? inParams = null;
        if (!string.IsNullOrEmpty(text))
            inParams = new object[] { text! };
        else if (item.ActionIn != null && item.ActionIn.Count > 0)
            inParams = item.ActionIn.ToArray();

        var ok = await _cloudService.CallMiotActionAsync(_device.Did, item.Siid, item.Aiid, inParams);
        if (ok)
        {
            await LoadControlsAsync();
            SetStatus($"已执行「{item.Label}」", "#4CAF50");
        }
        else
        {
            SetStatus($"「{item.Label}」执行失败，请检查网络或设备", "#F44336");
        }
    }

    private void ScheduleSliderSet(DeviceControlItem item, double value)
    {
        _pendingSlider = (item.Siid, item.Piid, value);
        _sliderTimer ??= new Timer(450) { AutoReset = false };
        _sliderTimer.Elapsed -= OnSliderElapsed;
        _sliderTimer.Elapsed += OnSliderElapsed;
        _sliderTimer.Stop();
        _sliderTimer.Start();
    }

    private async void OnSliderElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_pendingSlider is not { } p) return;
        _pendingSlider = null;
        if (_device == null) return;
        var ok = await _cloudService.SetMiotPropertyAsync(_device.Did, p.Siid, p.Piid, (int)Math.Round(p.Value));
        await Dispatcher.UIThread.InvokeAsync(() =>
            SetStatus(ok ? $"已设置「{p.Value:0}」" : "调节失败，请检查网络", ok ? "#4CAF50" : "#F44336"));
        if (ok) await LoadControlsAsync();
    }

    // === 基础控制（灯/开关/窗帘 回退，保留旧逻辑）===

    private async Task ToggleAsync(string did)
    {
        if (_status == null) return;
        var newState = !_status.IsPoweredOn;
        if (await _cloudService.SetPowerAsync(did, newState))
        {
            await RefreshStatusAsync();
            SetStatus($"已{(newState ? "开启" : "关闭")}：{_status.Name}", "#4CAF50");
        }
        else
        {
            SetStatus("设备控制失败，请检查网络", "#F44336");
        }
    }

    private async Task CurtainAsync(string did, string action)
    {
        if (await _cloudService.SetCurtainAsync(did, action))
        {
            await RefreshStatusAsync();
            SetStatus($"窗帘已{action}：{_status?.Name ?? did}", "#4CAF50");
        }
        else
        {
            SetStatus("窗帘控制失败", "#F44336");
        }
    }

    private void ScheduleBrightnessSet(string did, double value)
    {
        _pendingBrightness = (int)Math.Round(value);
        _brightnessTimer ??= new Timer(500) { AutoReset = false };
        _brightnessTimer.Elapsed -= OnBrightnessTimerElapsed;
        _brightnessTimer.Elapsed += OnBrightnessTimerElapsed;
        _brightnessTimer.Stop();
        _brightnessTimer.Start();
    }

    private async void OnBrightnessTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_pendingBrightness is not { } pending) return;
        _pendingBrightness = null;
        if (await _cloudService.SetBrightnessAsync(_did, pending))
        {
            await RefreshStatusAsync();
            SetStatus($"亮度已设为 {pending}%", "#4CAF50");
        }
        else
        {
            SetStatus("亮度调节失败", "#F44336");
        }
    }

    // === 辅助 ===

    private static bool IsTrue(object? v) => v is true || (v is string s && s is "true" or "on" or "1");

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        int i => i,
        float f => f,
        string s => double.TryParse(s, out var x) ? x : 0,
        _ => 0
    };

    private static bool ValuesEqual(object a, object? b)
    {
        if (b == null) return false;
        if (a is int ai && b is int bi) return ai == bi;
        if (a is double ad && b is double bd) return Math.Abs(ad - bd) < 1e-6;
        return a.ToString() == b.ToString();
    }

    private static string FormatValue(object? v, string? unit)
    {
        if (v == null) return "—";
        var s = v switch
        {
            bool bo => bo ? "开" : "关",
            double d => d % 1 == 0 ? $"{d:0}" : $"{d:F1}",
            int i => $"{i}",
            _ => v.ToString() ?? "—"
        };
        return string.IsNullOrEmpty(unit) ? s : $"{s}{unit}";
    }

    private void SetStatus(string text, string color)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            _statusText.Text = text;
            _statusText.Foreground = Brush.Parse(color);
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
        _brightnessTimer?.Stop();
        _brightnessTimer?.Dispose();
        _sliderTimer?.Stop();
        _sliderTimer?.Dispose();
        base.OnClosed(e);
    }
}
