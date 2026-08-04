using System.Collections.ObjectModel;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MiIsland.Models;
using MiIsland.Services;
using Timer = System.Timers.Timer;

namespace MiIsland.Views;

/// <summary>
/// 系统托盘图标点击后打开的独立控制窗口。内容与桌面组件一致：
/// 设备列表 + 开关/亮度/窗帘控制，复用 MiCloudService 与 DeviceCardBuilder。
/// 关闭窗口时仅 Hide（托盘可再次打开），只有"退出托盘"才会真正销毁。
/// </summary>
public class MiIslandControlWindow : Window
{
    private readonly MiCloudService _cloudService = MiCloudService.Instance;
    private readonly PluginSettings _settings;
    private readonly ObservableCollection<MiDeviceStatus> _deviceStatuses = new();
    private List<MiCloudDevice>? _cloudDevices;
    private Timer? _refreshTimer;
    private Timer? _brightnessTimer;
    private (string Did, int Value)? _pendingBrightness;
    private CancellationTokenSource? _refreshCts;

    private ItemsControl _devicesList = null!;
    private TextBlock _statusText = null!;
    private Button _refreshButton = null!;

    private bool _forceClose;

    public MiIslandControlWindow()
    {
        _settings = PluginSettings.Load();
        Title = "MiIsland 米家设备控制";
        Width = 380;
        Height = 560;
        MinWidth = 320;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUI();

        if (_settings.RefreshIntervalSeconds > 0)
        {
            _refreshTimer = new Timer(_settings.RefreshIntervalSeconds * 1000) { AutoReset = true };
            _refreshTimer.Elapsed += (_, _) => _ = RefreshAllAsync();
        }

        _ = RefreshAllAsync();
    }

    private void BuildUI()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(10)
        };

        // === 标题栏 ===
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        header.Children.Add(new TextBlock
        {
            Text = "MiIsland 米家设备",
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

        // === 设备列表 ===
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 200,
            Padding = new Thickness(0, 2, 0, 2)
        };
        _devicesList = new ItemsControl { MinHeight = 150 };
        _devicesList.ItemTemplate = new FuncDataTemplate<MiDeviceStatus>((status, _) => BuildDeviceCard(status));
        scroll.Content = _devicesList;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // === 状态栏 ===
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

    private Control BuildDeviceCard(MiDeviceStatus status)
    {
        Action<string> onToggle = did => { _ = ToggleDeviceAsync(did); };
        Action<string, string> onCurtain = (did, action) => { _ = CurtainDeviceAsync(did, action); };
        Action<string, double> onBrightness = (did, v) => ScheduleBrightnessSet(did, v);
        return DeviceCardBuilder.BuildCard(status, onToggle, onCurtain, onBrightness);
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        _refreshButton.IsEnabled = false;
        try
        {
            await RefreshAllAsync();
        }
        finally
        {
            _refreshButton.IsEnabled = true;
        }
    }

    private async Task RefreshAllAsync()
    {
        if (!_cloudService.IsLoggedIn)
        {
            SetStatus("未登录，请在 ClassIsland 插件设置中登录小米账号", "#FF9800");
            return;
        }

        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        try
        {
            if (_cloudDevices == null)
            {
                var (devices, err) = await _cloudService.GetDeviceListAsync();
                if (devices == null)
                {
                    SetStatus(string.IsNullOrEmpty(err)
                        ? "获取设备列表失败"
                        : $"获取设备列表失败：{err}", "#F44336");
                    return;
                }

                _cloudDevices = devices;
                var online = devices.Count(d => d.IsOnline);
                SetStatus(devices.Count == 0
                        ? "该账号下暂无米家设备"
                        : $"已连接 {devices.Count} 台设备（{online} 台在线）",
                    devices.Count == 0 ? "#9E9E9E" : "#4CAF50");
            }

            var enabled = 0;
            foreach (var device in _cloudDevices)
            {
                if (ct.IsCancellationRequested) break;
                if (!device.IsOnline) continue;
                if (!_settings.IsDeviceEnabled(device.Did)) continue;
                enabled++;

                var status = await _cloudService.GetDeviceStatusAsync(device);
                if (status == null) continue;

                Upsert(status);
                await Dispatcher.UIThread.InvokeAsync(RefreshUi);
            }

            if (enabled == 0 && _cloudDevices.Any(d => d.IsOnline))
            {
                SetStatus("暂无启用的设备，请在设置中勾选要显示的设备", "#9E9E9E");
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
                _statusText.Text = _statusText.Text + $"　更新于 {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiIsland] window refresh error: {ex.Message}");
        }
    }

    private void Upsert(MiDeviceStatus status)
    {
        for (var i = 0; i < _deviceStatuses.Count; i++)
        {
            if (_deviceStatuses[i].Did == status.Did)
            {
                _deviceStatuses[i] = status;
                return;
            }
        }
        _deviceStatuses.Add(status);
    }

    private void RefreshUi()
    {
        _devicesList.ItemsSource = null;
        _devicesList.ItemsSource = _deviceStatuses;
    }

    private void SetStatus(string text, string color)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            _statusText.Text = text;
            _statusText.Foreground = Brush.Parse(color);
        });
    }

    // === 控制 (操作后回查真实状态) ===

    private async Task ToggleDeviceAsync(string did)
    {
        var status = _deviceStatuses.FirstOrDefault(d => d.Did == did);
        if (status == null) return;

        var newState = !status.IsPoweredOn;
        var ok = await _cloudService.SetPowerAsync(did, newState);
        if (ok)
        {
            await RequeryDeviceAsync(did);
            SetStatus($"已{(newState ? "开启" : "关闭")}：{status.Name}", "#4CAF50");
        }
        else
        {
            SetStatus("设备控制失败，请检查网络", "#F44336");
        }
    }

    private async Task CurtainDeviceAsync(string did, string action)
    {
        var ok = await _cloudService.SetCurtainAsync(did, action);
        if (ok)
        {
            await RequeryDeviceAsync(did);
            SetStatus($"窗帘已{action}：{did}", "#4CAF50");
        }
        else
        {
            SetStatus("窗帘控制失败", "#F44336");
        }
    }

    private void ScheduleBrightnessSet(string did, double value)
    {
        _pendingBrightness = (did, (int)Math.Round(value));

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

        var ok = await _cloudService.SetBrightnessAsync(pending.Did, pending.Value);
        if (ok)
        {
            await RequeryDeviceAsync(pending.Did);
            SetStatus($"亮度已设为 {pending.Value}%", "#4CAF50");
        }
        else
        {
            SetStatus("亮度调节失败", "#F44336");
        }
    }

    private async Task RequeryDeviceAsync(string did)
    {
        var device = _cloudDevices?.FirstOrDefault(d => d.Did == did);
        if (device == null) return;

        var newStatus = await _cloudService.GetDeviceStatusAsync(device);
        if (newStatus == null) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Upsert(newStatus);
            RefreshUi();
        });
    }

    // === 关闭行为: 仅隐藏, 由托盘再次打开 ===

    public void RequestClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
        _brightnessTimer?.Stop();
        _brightnessTimer?.Dispose();
        base.OnClosing(e);
    }
}
