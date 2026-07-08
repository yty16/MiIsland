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
    description: "云端控制米家智能设备，无需局域网和Token，小米账号登录即可"
)]
public class MiHomeComponent : ComponentBase
{
    private readonly MiCloudService _cloudService = new();
    private readonly PluginSettings _settings;
    private readonly ObservableCollection<MiDeviceStatus> _deviceStatuses = new();
    private List<MiCloudDevice>? _cloudDevices;
    private Timer? _refreshTimer;
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

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto")
            };

            // 状态圆点
            var dot = new Border
            {
                Width = 10, Height = 10,
                CornerRadius = new CornerRadius(5),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            dot.Bind(Border.BackgroundProperty, new Avalonia.Data.Binding("StatusColor"));
            Grid.SetRowSpan(dot, 2);
            grid.Children.Add(dot);

            // 设备名
            var nameText = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeight.Medium,
                Foreground = Brush.Parse("#333333")
            };
            nameText.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Name"));
            Grid.SetRow(nameText, 0);
            Grid.SetColumn(nameText, 1);
            grid.Children.Add(nameText);

            // 状态文本
            var statusText = new TextBlock
            {
                FontSize = 11,
                Foreground = Brush.Parse("#9E9E9E"),
                Margin = new Thickness(0, 2, 0, 0)
            };
            statusText.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("StatusText"));
            Grid.SetRow(statusText, 1);
            Grid.SetColumn(statusText, 1);
            grid.Children.Add(statusText);

            // 开关
            var toggleBtn = new Button
            {
                Width = 44, Height = 30,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Content = "开关",
                Background = Brush.Parse("#E0E0E0"),
                Foreground = Brush.Parse("#333333"),
                CornerRadius = new CornerRadius(4),
                Tag = status.Did
            };
            toggleBtn.Click += OnDeviceToggleClick;
            Grid.SetRowSpan(toggleBtn, 2);
            Grid.SetColumn(toggleBtn, 2);
            grid.Children.Add(toggleBtn);

            card.Child = grid;
            return card;
        });
    }

    // === 自动登录并刷新 ===

    private async Task AutoLoginAndRefreshAsync()
    {
        // 如果有已保存的账号，自动登录
        if (!string.IsNullOrEmpty(_settings.Account.Username) &&
            !string.IsNullOrEmpty(_settings.Account.Password))
        {
            await _cloudService.LoginAsync(
                _settings.Account.Username,
                _settings.Account.Password);
        }

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
                        var existing = _deviceStatuses.FirstOrDefault(
                            d => d.Did == status.Did);
                        if (existing != null)
                        {
                            var idx = _deviceStatuses.IndexOf(existing);
                            _deviceStatuses[idx] = status;
                        }
                        else
                        {
                            _deviceStatuses.Add(status);
                        }

                        _devicesList!.ItemsSource = _deviceStatuses;

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

    // === 开关控制 ===

    private async void OnDeviceToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string did)
            return;

        var status = _deviceStatuses.FirstOrDefault(d => d.Did == did);
        if (status == null) return;

        var newState = !status.IsPoweredOn;
        var success = await _cloudService.SetPowerAsync(did, newState);

        if (success)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                status.IsPoweredOn = newState;
                status.LastUpdated = DateTime.Now;
                var idx = _deviceStatuses.IndexOf(status);
                if (idx >= 0)
                    _deviceStatuses[idx] = status;
            });
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_statusText != null)
                {
                    _statusText.Text = "设备控制失败，请检查网络";
                    _statusText.Foreground = Brush.Parse("#F44336");
                }
            });
        }
    }

    // === 清理 ===

    private void OnComponentUnloaded(object? sender, EventArgs e)
    {
        _refreshCts?.Cancel();
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
        _cloudService.Dispose();
        this.Unloaded -= OnComponentUnloaded;
    }
}
