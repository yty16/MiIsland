using System.Collections.ObjectModel;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MiIsland.Models;
using MiIsland.Services;
using Timer = System.Timers.Timer;

namespace MiIsland.Views;

[ComponentInfo(
    "A1E4F8B2-6C3D-4E5F-9A1B-2C3D4E5F6A7B",
    "米家设备面板",
    description: "云端控制米家智能设备，仅需小米官方扫码登录即可授权"
)]
public class MiHomeComponent : ComponentBase
{
    private readonly MiCloudService _cloudService = MiCloudService.Instance;
    private readonly PluginSettings _settings;
    private readonly ObservableCollection<MiDeviceStatus> _deviceStatuses = new();
    private List<MiCloudDevice>? _cloudDevices;
    private Timer? _refreshTimer;
    private Timer? _brightnessTimer;
    private (string Did, int Value)? _pendingBrightness;
    private CancellationTokenSource? _refreshCts;

    // UI控件
    private Grid? _rootGrid;
    private ItemsControl? _devicesList;
    private TextBlock? _statusText;
    private TextBlock? _refreshTimeText;
    private Button? _refreshButton;

    public MiHomeComponent()
    {
        _settings = PluginSettings.Load();
        this.Unloaded += OnComponentUnloaded;
        MiCloudService.LoginStateChanged += OnLoginStateChanged;
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        BuildUI();

        if (_settings.RefreshIntervalSeconds > 0)
        {
            _refreshTimer = new Timer(_settings.RefreshIntervalSeconds * 1000);
            _refreshTimer.Elapsed += OnRefreshTimerElapsed;
            _refreshTimer.AutoReset = true;
        }

        _ = AutoLoginAndRefreshAsync();
    }

    private void BuildUI()
    {
        _rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(8),
            MinHeight = 60
        };

        // === 标题栏 ===
        var titleBar = BuildTitleBar();
        Grid.SetRow(titleBar, 0);
        _rootGrid.Children.Add(titleBar);

        // === 设备列表 ===
        _devicesList = new ItemsControl();
        _devicesList.ItemTemplate = BuildDeviceTemplate();
        Grid.SetRow(_devicesList, 1);
        _rootGrid.Children.Add(_devicesList);

        // === 状态栏 ===
        _statusText = new TextBlock
        {
            Text = "未登录，请在插件设置中登录小米账号",
            FontSize = 11,
            Foreground = Brush.Parse("#FF9800"),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 2),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_statusText, 2);
        _rootGrid.Children.Add(_statusText);

        Content = _rootGrid;
    }

    private Border BuildTitleBar()
    {
        var border = new Border
        {
            Background = Brush.Parse("#1A000000"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        // 图标+标题
        panel.Children.Add(new TextBlock
        {
            Text = "米家设备",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#333333"),
            VerticalAlignment = VerticalAlignment.Center
        });

        // 更新时间
        _refreshTimeText = new TextBlock
        {
            Text = "",
            FontSize = 10,
            Foreground = Brush.Parse("#999999"),
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(_refreshTimeText);

        // 刷新按钮
        _refreshButton = new Button
        {
            Content = "刷新",
            FontSize = 10,
            Padding = new Thickness(8, 2),
            Background = Brush.Parse("#E0E0E0"),
            Foreground = Brush.Parse("#666666"),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _refreshButton.Click += OnRefreshClick;
        panel.Children.Add(_refreshButton);

        border.Child = panel;
        return border;
    }

    private FuncDataTemplate<MiDeviceStatus> BuildDeviceTemplate()
    {
        return new FuncDataTemplate<MiDeviceStatus>((status, _) =>
        {
            var card = new Border
            {
                Margin = new Thickness(2),
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(6),
                Background = Brush.Parse("#1A000000")
            };

            var root = new StackPanel { Spacing = 4 };

            // === 第一行: 状态点 + 名称 + 状态文字 + 右侧控件 ===
            var top = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto")
            };

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
                toggleBtn.Click += OnDeviceToggleClick;
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
                    b.Click += OnCurtainClick;
                    panel.Children.Add(b);
                }
                Grid.SetRowSpan(panel, 2);
                Grid.SetColumn(panel, 2);
                top.Children.Add(panel);
            }
            // Sensor: 只读展示, 无控件

            root.Children.Add(top);

            // === 灯: 亮度滑块 (先设值再挂事件, 避免初始赋值误触发 RPC) ===
            if (status.Kind == MiDeviceKind.Light)
            {
                var slider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Margin = new Thickness(0, 6, 0, 0),
                    Tag = status.Did
                };
                var didLocal = status.Did;
                slider.Value = Math.Clamp(status.Brightness ?? 50, 0, 100);
                slider.ValueChanged += (_, _) => ScheduleBrightnessSet(didLocal, slider.Value);
                root.Children.Add(slider);
            }

            card.Child = root;
            return card;
        });
    }

    // === 自动登录并刷新 ===

    private async Task AutoLoginAndRefreshAsync()
    {
        // 登录态已持久化到插件目录 (卸载即失效), 启动若会话仍有效则直接拉设备。
        if (_cloudService.IsLoggedIn)
        {
            _refreshTimer?.Start();
            await RefreshAllAsync();
        }
    }

    // === 设备刷新 ===

    private async void OnRefreshTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        await RefreshAllAsync();
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshButton != null) _refreshButton.IsEnabled = false;
        try
        {
            await RefreshAllAsync();
        }
        finally
        {
            if (_refreshButton != null) _refreshButton.IsEnabled = true;
        }
    }

    private async Task RefreshAllAsync()
    {
        if (!_cloudService.IsLoggedIn)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_statusText != null)
                {
                    _statusText.Text = "未登录，请在插件设置中登录小米账号";
                    _statusText.Foreground = Brush.Parse("#FF9800");
                }
            });
            return;
        }

        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        try
        {
            // 先获取设备列表
            if (_cloudDevices == null)
            {
                var (devices, _) = await _cloudService.GetDeviceListAsync();
                if (devices != null)
                {
                    _cloudDevices = devices;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_statusText != null)
                        {
                            _statusText.Text = $"已连接 {devices.Count} 台设备";
                            _statusText.Foreground = Brush.Parse("#4CAF50");
                        }
                    });
                }
            }

            // 刷新每个在线设备的状态
            if (_cloudDevices != null)
            {
                foreach (var device in _cloudDevices)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!device.IsOnline) continue;
                    if (!_settings.IsDeviceEnabled(device.Did)) continue;

                    var status = await _cloudService.GetDeviceStatusAsync(device);
                    if (status == null) continue;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        UpsertStatus(status);
                        RefreshDeviceListUi();

                        if (_refreshTimeText != null)
                            _refreshTimeText.Text = $"更新于 {DateTime.Now:HH:mm:ss}";
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MiHome] RefreshAllAsync error: {ex.Message}");
        }
    }

    /// <summary>按 did 更新或新增一条状态</summary>
    private void UpsertStatus(MiDeviceStatus status)
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

    /// <summary>强制整列重新渲染 (模板为一次性绑定, 重新挂 ItemsSource 才能反映最新状态)</summary>
    private void RefreshDeviceListUi()
    {
        if (_devicesList == null) return;
        _devicesList.ItemsSource = null;
        _devicesList.ItemsSource = _deviceStatuses;
    }

    private void SetStatusText(string text, string color)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_statusText != null)
            {
                _statusText.Text = text;
                _statusText.Foreground = Brush.Parse(color);
            }
        });
    }

    // === 开关 / 亮度 / 窗帘控制 (操作后回查真实状态) ===

    private async void OnDeviceToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string did) return;
        var status = _deviceStatuses.FirstOrDefault(d => d.Did == did);
        if (status == null) return;

        var newState = !status.IsPoweredOn;
        button.IsEnabled = false;
        try
        {
            var ok = await _cloudService.SetPowerAsync(did, newState);
            if (ok)
            {
                await RequeryDeviceAsync(did);
                SetStatusText($"已{(newState ? "开启" : "关闭")}：{status.Name}", "#4CAF50");
            }
            else
            {
                SetStatusText("设备控制失败，请检查网络", "#F44336");
            }
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnCurtainClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        var parts = tag.Split(':');
        if (parts.Length != 2) return;
        var did = parts[0];
        var action = parts[1];

        button.IsEnabled = false;
        try
        {
            var ok = await _cloudService.SetCurtainAsync(did, action);
            if (ok)
            {
                await RequeryDeviceAsync(did);
                SetStatusText($"窗帘已{action}：{did}", "#4CAF50");
            }
            else
            {
                SetStatusText("窗帘控制失败", "#F44336");
            }
        }
        finally
        {
            button.IsEnabled = true;
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
            SetStatusText($"亮度已设为 {pending.Value}%", "#4CAF50");
        }
        else
        {
            SetStatusText("亮度调节失败", "#F44336");
        }
    }

    /// <summary>操作后回查设备真实状态并刷新 UI</summary>
    private async Task RequeryDeviceAsync(string did)
    {
        var device = _cloudDevices?.FirstOrDefault(d => d.Did == did);
        if (device == null) return;

        var newStatus = await _cloudService.GetDeviceStatusAsync(device);
        if (newStatus == null) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            UpsertStatus(newStatus);
            RefreshDeviceListUi();
        });
    }

    // === 清理 ===

    private void OnComponentUnloaded(object? sender, EventArgs e)
    {
        _refreshCts?.Cancel();
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
        _brightnessTimer?.Stop();
        _brightnessTimer?.Dispose();
        // 注意：_cloudService 是全局共享单例，不能在组件卸载时 Dispose，否则会破坏登录会话。
        MiCloudService.LoginStateChanged -= OnLoginStateChanged;
        this.Unloaded -= OnComponentUnloaded;
    }

    /// <summary>登录状态变化（设置页扫码成功 / 登出）时，自动刷新设备列表并启动定时器。</summary>
    private void OnLoginStateChanged(object? sender, EventArgs e)
    {
        if (_cloudService.IsLoggedIn)
        {
            _refreshTimer?.Start();
        }
        _ = RefreshAllAsync();
    }
}
