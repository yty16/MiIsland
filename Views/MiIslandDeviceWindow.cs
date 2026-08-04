using System.Collections.Generic;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MiIsland.Models;
using MiIsland.Services;
using Timer = System.Timers.Timer;

namespace MiIsland.Views;

/// <summary>
/// 单个设备控制窗口。由桌面快捷方式 classisland://plugins/MiIsland/device/{did} 打开，
/// 仅展示并控制该设备（开关 / 亮度 / 窗帘），操作后回查真实状态。
/// 每个 did 对应一个复用窗口实例（按 did 缓存）。
/// </summary>
public class MiIslandDeviceWindow : Window
{
    private readonly MiCloudService _cloudService = MiCloudService.Instance;
    private readonly PluginSettings _settings = PluginSettings.Load();
    private readonly string _did;

    private MiCloudDevice? _device;
    private MiDeviceStatus? _status;

    private Timer? _refreshTimer;
    private Timer? _brightnessTimer;
    private int? _pendingBrightness;

    private StackPanel _cardHost = null!;
    private TextBlock _statusText = null!;
    private Button _refreshButton = null!;

    private static readonly Dictionary<string, MiIslandDeviceWindow> Instances = new();

    public MiIslandDeviceWindow(string did)
    {
        _did = did;
        Title = "MiIsland 设备";
        Width = 360;
        Height = 320;
        MinWidth = 300;
        MinHeight = 240;
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
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        header.Children.Add(new TextBlock
        {
            Text = "MiIsland 单设备控制",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#333333"),
            VerticalAlignment = VerticalAlignment.Center
        });
        _refreshButton = new Button
        {
            Content = "刷新",
            Padding = new Thickness(12, 4),
            Background = Brush.Parse("#E0E0E0"),
            Foreground = Brush.Parse("#333333"),
            CornerRadius = new CornerRadius(4)
        };
        _refreshButton.Click += OnRefreshClick;
        Grid.SetColumn(_refreshButton, 1);
        header.Children.Add(_refreshButton);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // 设备卡片容器
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 2, 0, 2)
        };
        _cardHost = new StackPanel { Spacing = 6 };
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

        if (_settings.RefreshIntervalSeconds > 0)
        {
            _refreshTimer = new Timer(_settings.RefreshIntervalSeconds * 1000) { AutoReset = true };
            _refreshTimer.Elapsed += (_, _) => _ = RefreshStatusAsync();
        }

        await RefreshStatusAsync();
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
            await Dispatcher.UIThread.InvokeAsync(RenderCard);
            SetStatus($"更新于 {DateTime.Now:HH:mm:ss}", "#4CAF50");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiIsland] device window refresh: {ex.Message}");
        }
    }

    private void RenderCard()
    {
        if (_status == null) return;

        Action<string> onToggle = did => { _ = ToggleAsync(did); };
        Action<string, string> onCurtain = (did, action) => { _ = CurtainAsync(did, action); };
        Action<string, double> onBrightness = (did, v) => ScheduleBrightnessSet(did, v);

        _cardHost.Children.Clear();
        _cardHost.Children.Add(DeviceCardBuilder.BuildCard(_status, onToggle, onCurtain, onBrightness));
    }

    // === 控制（操作后回查真实状态）===

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
        base.OnClosed(e);
    }
}
