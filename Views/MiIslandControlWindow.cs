using System.Collections.ObjectModel;
using System.IO;
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
    private PluginSettings _settings;
    private readonly ObservableCollection<MiDeviceStatus> _deviceStatuses = new();
    private List<MiCloudDevice>? _cloudDevices;
    private Timer? _refreshTimer;
    private Timer? _brightnessTimer;
    private (string Did, int Value)? _pendingBrightness;
    private CancellationTokenSource? _refreshCts;
    private FileSystemWatcher? _settingsWatcher;

    private ItemsControl _devicesList = null!;
    private TextBlock _statusText = null!;
    private Button _refreshButton = null!;

    private bool _forceClose;

    // 单例：系统托盘与 Uri 导航共用同一个总控窗口实例。
    private static MiIslandControlWindow? _instance;

    /// <summary>显示/激活设备总控窗口（系统托盘与 classisland://plugins/MiIsland/control 共用）。</summary>
    public static void ShowControlWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_instance == null)
            {
                _instance = new MiIslandControlWindow();
                _instance.Closed += (_, _) => { _instance = null; };
            }
            _instance.Show();
            _instance.Activate();
        });
    }

    /// <summary>强制关闭总控窗口（退出托盘时调用）。</summary>
    public static void CloseInstance()
    {
        Dispatcher.UIThread.Post(() => _instance?.RequestClose());
    }

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

        // 根据设置初始化自动刷新定时器（间隔改动后无需重启即生效）
        ApplyRefreshInterval();
        // 监听设置文件变更：刷新间隔 / 设备启用状态改动后自动套用
        SetupSettingsWatcher();

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

    /// <summary>按当前设置套用自动刷新定时器：间隔=0 停止；>0 动态更新间隔并启动（无需重启）。</summary>
    private void ApplyRefreshInterval()
    {
        _refreshTimer ??= new Timer { AutoReset = true };
        _refreshTimer.Elapsed -= OnRefreshTimerElapsed;
        _refreshTimer.Elapsed += OnRefreshTimerElapsed;

        var sec = _settings.RefreshIntervalSeconds;
        if (sec <= 0)
        {
            _refreshTimer.Stop();
            return;
        }
        _refreshTimer.Interval = Math.Max(1, sec) * 1000;
        if (_cloudService.IsLoggedIn)
            _refreshTimer.Start();
    }

    private void OnRefreshTimerElapsed(object? sender, ElapsedEventArgs e)
        => _ = RefreshAllAsync();

    /// <summary>监听 settings.json 变更，间隔 / 设备启用状态改动后即时套用。</summary>
    private void SetupSettingsWatcher()
    {
        try
        {
            _settingsWatcher = new FileSystemWatcher(PluginSettings.SettingsFilePath)
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _settingsWatcher.Changed += (_, _) => OnSettingsFileChanged();
        }
        catch
        {
            // 不支持文件监听时忽略，间隔改动仍需重启生效（不影响其他功能）
        }
    }

    private void OnSettingsFileChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try { _settings = PluginSettings.Load(); } catch { /* 读取失败保留旧设置 */ }
            ApplyRefreshInterval();
        });
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
            var failCount = 0;
            var iconPairs = new System.Collections.Generic.List<(MiDeviceStatus, MiCloudDevice)>();
            foreach (var device in _cloudDevices)
            {
                if (ct.IsCancellationRequested) break;
                if (!device.IsOnline) continue;
                if (!_settings.IsDeviceEnabled(device.Did)) continue;
                enabled++;

                var status = await _cloudService.GetDeviceStatusAsync(device);
                if (status == null) { failCount++; continue; }

                // 自定义图标即时生效；云端图标稍后并行补齐
                status.IconPath = _settings.GetDeviceIcon(device.Did);
                iconPairs.Add((status, device));

                Upsert(status);
                await Dispatcher.UIThread.InvokeAsync(RefreshUi);
            }

            // 并行补齐云端设备图标，完成后整列重绘一次
            if (iconPairs.Count > 0)
                await ResolveCloudIconsAsync(iconPairs);

            if (enabled == 0 && _cloudDevices.Any(d => d.IsOnline))
            {
                SetStatus("暂无启用的设备，请在设置中勾选要显示的设备", "#9E9E9E");
            }
            else if (failCount > 0 && enabled == failCount)
            {
                // 全部启用设备刷新失败：把云端真实错误透出到窗口状态栏
                SetStatus($"⚠ 设备状态刷新失败：{_cloudService.LastError ?? "未知错误"}", "#F44336");
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
                _statusText.Text = _statusText.Text + $"　更新于 {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            SetStatus($"刷新出错：{_cloudService.LastError ?? ex.Message}", "#F44336");
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

        // 恢复图标（自定义优先；云端图已缓存时即时返回）
        newStatus.IconPath = _settings.GetDeviceIcon(did)
                          ?? await DeviceImageHelper.GetCloudImagePathAsync(device);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Upsert(newStatus);
            RefreshUi();
        });
    }

    /// <summary>并行补齐云端设备图标；完成后整列重绘一次。</summary>
    private async Task ResolveCloudIconsAsync(
        System.Collections.Generic.List<(MiDeviceStatus status, MiCloudDevice device)> pairs)
    {
        await System.Threading.Tasks.Task.WhenAll(pairs.Select(async p =>
        {
            if (!string.IsNullOrEmpty(p.status.IconPath)) return;
            var path = await DeviceImageHelper.GetCloudImagePathAsync(p.device);
            if (!string.IsNullOrEmpty(path))
                p.status.IconPath = path;
        }));
        await Dispatcher.UIThread.InvokeAsync(RefreshUi);
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
        _settingsWatcher?.Dispose();
        base.OnClosing(e);
    }
}
