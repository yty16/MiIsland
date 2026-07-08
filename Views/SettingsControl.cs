using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MiIsland.Models;
using MiIsland.Services;

namespace MiIsland.Views;

[SettingsPageInfo("F2A9C1E3-7B4D-4F8A-9C2E-1D3F5A7B9C0E", "MiIsland 设置")]
public partial class SettingsControl : SettingsPageBase
{
    private readonly PluginSettings _settings;
    private readonly MiCloudService _cloudService = new();

    // UI 控件引用
    private TextBox? _usernameBox;
    private TextBox? _passwordBox;
    private ComboBox? _countryCombo;
    private Button? _loginButton;
    private Button? _qrButton;
    private Button? _logoutButton;
    private TextBlock? _loginStatusText;
    private TextBlock? _deviceCountText;
    private NumericUpDown? _refreshIntervalBox;
    private ItemsControl? _deviceListControl;

    // 扫码登录 UI
    private Border? _qrPanel;
    private Image? _qrImage;
    private TextBlock? _qrStatusText;
    private Button? _qrCancelButton;

    public SettingsControl()
    {
        _settings = PluginSettings.Load();
        this.Unloaded += OnPageUnloaded;
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        BuildUI();
        UpdateLoginStatus();
    }

    private void BuildUI()
    {
        var scroll = new ScrollViewer();
        var rootPanel = new StackPanel { Spacing = 16, Margin = new Thickness(16) };

        // === 标题 ===
        rootPanel.Children.Add(new TextBlock
        {
            Text = "米家设备设置",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        // === 登录区域 ===
        rootPanel.Children.Add(BuildLoginSection());

        // === 登录状态 ===
        _loginStatusText = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        rootPanel.Children.Add(_loginStatusText);

        // === 设备列表 ===
        rootPanel.Children.Add(BuildDeviceListSection());

        // === 刷新间隔 ===
        rootPanel.Children.Add(BuildRefreshSection());

        scroll.Content = rootPanel;
        Content = scroll;
    }

    private Border BuildSectionHeader(string title)
    {
        return new Border
        {
            BorderBrush = Brush.Parse("#E0E0E0"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 6),
            Margin = new Thickness(0, 8, 0, 4),
            Child = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#555555")
            }
        };
    }

    private StackPanel BuildLoginSection()
    {
        var section = new StackPanel { Spacing = 8 };

        section.Children.Add(BuildSectionHeader("小米账号登录"));

        // 地区选择
        var countryRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("80,*"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        countryRow.Children.Add(new TextBlock
        {
            Text = "地区",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        });
        _countryCombo = new ComboBox
        {
            ItemsSource = new[] { "中国大陆 (cn)", "其他地区" },
            SelectedIndex = 0
        };
        Grid.SetColumn(_countryCombo, 1);
        countryRow.Children.Add(_countryCombo);
        section.Children.Add(countryRow);

        // 账号
        var userRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("80,*"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        userRow.Children.Add(new TextBlock
        {
            Text = "小米账号",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        });
        _usernameBox = new TextBox
        {
            Watermark = "手机号/邮箱/Xiaomi ID",
            Text = _settings.Account.Username
        };
        Grid.SetColumn(_usernameBox, 1);
        userRow.Children.Add(_usernameBox);
        section.Children.Add(userRow);

        // 密码
        var passRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("80,*"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        passRow.Children.Add(new TextBlock
        {
            Text = "密码",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        });
        _passwordBox = new TextBox
        {
            Watermark = "小米账号密码",
            PasswordChar = '*',
            Text = _settings.Account.Password
        };
        Grid.SetColumn(_passwordBox, 1);
        passRow.Children.Add(_passwordBox);
        section.Children.Add(passRow);

        // 按钮行
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(80, 4, 0, 0)
        };

        _loginButton = new Button
        {
            Content = "登  录",
            Width = 80,
            Background = Brush.Parse("#4CAF50"),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(4),
            FontWeight = FontWeight.SemiBold
        };
        _loginButton.Click += OnLoginClick;
        buttonRow.Children.Add(_loginButton);

        _logoutButton = new Button
        {
            Content = "登出",
            Width = 80,
            Background = Brush.Parse("#E0E0E0"),
            Foreground = Brush.Parse("#666666"),
            CornerRadius = new CornerRadius(4)
        };
        _logoutButton.Click += OnLogoutClick;
        buttonRow.Children.Add(_logoutButton);

        section.Children.Add(buttonRow);

        // 说明文字
        section.Children.Add(new TextBlock
        {
            Text = "登录后将自动获取您小米账号下的所有米家智能设备。\n密码仅保存在本地，不会上传到任何第三方。",
            FontSize = 11,
            Foreground = Brush.Parse("#999999"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(80, 4, 0, 0)
        });

        // 扫码登录按钮
        var qrBtnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(80, 6, 0, 0)
        };
        _qrButton = new Button
        {
            Content = "扫码登录",
            Width = 100,
            Background = Brush.Parse("#2196F3"),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(4),
            FontWeight = FontWeight.SemiBold
        };
        _qrButton.Click += OnQrLoginClick;
        qrBtnRow.Children.Add(_qrButton);
        section.Children.Add(qrBtnRow);

        // 扫码登录面板（初始隐藏）
        _qrPanel = new Border
        {
            Background = Brush.Parse("#F5F5F5"),
            BorderBrush = Brush.Parse("#E0E0E0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(80, 8, 0, 0),
            IsVisible = false
        };
        var qrStack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };

        _qrImage = new Image
        {
            Width = 200,
            Height = 200,
            Stretch = Stretch.Uniform
        };
        qrStack.Children.Add(_qrImage);

        _qrStatusText = new TextBlock
        {
            Text = "正在获取二维码...",
            FontSize = 12,
            Foreground = Brush.Parse("#555555"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };
        qrStack.Children.Add(_qrStatusText);

        _qrCancelButton = new Button
        {
            Content = "取消",
            Width = 80,
            Background = Brush.Parse("#E0E0E0"),
            Foreground = Brush.Parse("#666666"),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _qrCancelButton.Click += OnQrCancelClick;
        qrStack.Children.Add(_qrCancelButton);

        _qrPanel.Child = qrStack;
        section.Children.Add(_qrPanel);

        return section;
    }

    private StackPanel BuildDeviceListSection()
    {
        var section = new StackPanel { Spacing = 6 };

        section.Children.Add(BuildSectionHeader("云端设备列表"));

        // 刷新按钮
        var refreshRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        _deviceCountText = new TextBlock
        {
            Text = "请先登录以获取设备列表",
            FontSize = 12,
            Foreground = Brush.Parse("#999999"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var refreshDevBtn = new Button
        {
            Content = "刷新设备列表",
            FontSize = 11,
            Padding = new Thickness(10, 3),
            Background = Brush.Parse("#E0E0E0"),
            CornerRadius = new CornerRadius(3)
        };
        refreshDevBtn.Click += OnRefreshDevicesClick;
        refreshRow.Children.Add(_deviceCountText);
        refreshRow.Children.Add(refreshDevBtn);
        section.Children.Add(refreshRow);

        // 设备列表
        _deviceListControl = new ItemsControl
        {
            Margin = new Thickness(0, 4, 0, 0)
        };
        section.Children.Add(_deviceListControl);

        return section;
    }

    private StackPanel BuildRefreshSection()
    {
        var section = new StackPanel { Spacing = 6 };

        section.Children.Add(BuildSectionHeader("刷新设置"));

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        row.Children.Add(new TextBlock
        {
            Text = "自动刷新间隔",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        });
        _refreshIntervalBox = new NumericUpDown
        {
            Value = _settings.RefreshIntervalSeconds,
            Minimum = 0,
            Maximum = 300,
            Width = 80
        };
        row.Children.Add(_refreshIntervalBox);
        row.Children.Add(new TextBlock
        {
            Text = "秒 (0=关闭)",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = Brush.Parse("#999999")
        });
        section.Children.Add(row);

        return section;
    }

    // === 事件处理 ===

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        if (_loginButton == null) return;

        var username = _usernameBox?.Text?.Trim() ?? "";
        var password = _passwordBox?.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            _loginStatusText!.Text = "⚠ 请输入小米账号和密码";
            _loginStatusText.Foreground = Brush.Parse("#FF9800");
            return;
        }

        _loginButton.IsEnabled = false;
        _loginButton.Content = "登录中...";
        _loginStatusText!.Text = "正在登录小米账号...";
        _loginStatusText.Foreground = Brush.Parse("#2196F3");

        try
        {
            var (success, message) = await _cloudService.LoginAsync(username, password);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (success)
                {
                    _settings.Account.Username = username;
                    _settings.Account.Password = password;
                    _settings.Save();

                    _loginStatusText.Text = "✓ 登录成功";
                    _loginStatusText.Foreground = Brush.Parse("#4CAF50");

                    _ = RefreshDeviceListAsync();
                }
                else
                {
                    _loginStatusText.Text = $"✗ {message}";
                    _loginStatusText.Foreground = Brush.Parse("#F44336");
                }
                UpdateLoginStatus();
            });
        }
        finally
        {
            _loginButton.IsEnabled = true;
            _loginButton.Content = "登  录";
        }
    }

    private void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        _cloudService.Logout();
        _settings.Account = new MiAccountInfo();
        _settings.Save();

        UpdateLoginStatus();
        _deviceCountText!.Text = "请先登录以获取设备列表";
        _deviceListControl!.ItemsSource = null;

        if (_usernameBox != null) _usernameBox.Text = "";
        if (_passwordBox != null) _passwordBox.Text = "";
    }

    // === 扫码登录 ===

    private async void OnQrLoginClick(object? sender, RoutedEventArgs e)
    {
        if (_qrButton == null || _qrPanel == null) return;

        _qrButton.IsEnabled = false;
        _qrButton.Content = "获取中...";
        _qrPanel.IsVisible = true;
        _qrStatusText!.Text = "正在获取二维码...";
        _qrImage!.Source = null;

        // Step 1: 请求二维码
        var qrInfo = await _cloudService.RequestQrCodeAsync();
        if (qrInfo == null || qrInfo.Value.QrImageUrl == null)
        {
            _qrStatusText.Text = $"✗ {qrInfo?.Error ?? "获取二维码失败"}";
            _qrButton.IsEnabled = true;
            _qrButton.Content = "扫码登录";
            return;
        }

        // 下载并显示二维码图片
        try
        {
            using var stream = await _cloudService.DownloadImageAsync(qrInfo.Value.QrImageUrl);
            var bitmap = new Bitmap(stream);
            _qrImage.Source = bitmap;
            _qrStatusText.Text = "请使用小米商城/米家 App 扫码登录";
        }
        catch (Exception ex)
        {
            _qrStatusText.Text = $"✗ 二维码图片加载失败: {ex.Message}";
            _qrButton.IsEnabled = true;
            _qrButton.Content = "扫码登录";
            return;
        }

        // Step 2: 长轮询等待扫码（使用 Progress 更新状态）
        var progress = new Progress<string>(msg =>
        {
            Dispatcher.UIThread.Post(() => { _qrStatusText!.Text = msg; });
        });

        var pollResult = await _cloudService.PollQrLoginAsync(
            qrInfo.Value.PollUrl!, qrInfo.Value.Timeout, progress);

        if (!pollResult.Success || pollResult.Location == null)
        {
            _qrStatusText.Text = $"✗ {pollResult.Message}";
            _qrButton.IsEnabled = true;
            _qrButton.Content = "扫码登录";
            return;
        }

        // Step 3: 完成登录
        var (success, message) = await _cloudService.CompleteQrLoginAsync(pollResult.Location);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (success)
            {
                _settings.Account.Username = "[扫码登录]";
                _settings.Save();
                _qrStatusText!.Text = "✓ 扫码登录成功";
                _qrStatusText.Foreground = Brush.Parse("#4CAF50");
                _qrPanel.IsVisible = false;
                _qrButton.IsEnabled = true;
                _qrButton.Content = "扫码登录";
                UpdateLoginStatus();
                _ = RefreshDeviceListAsync();
            }
            else
            {
                _qrStatusText!.Text = $"✗ {message}";
                _qrStatusText.Foreground = Brush.Parse("#F44336");
                _qrButton.IsEnabled = true;
                _qrButton.Content = "扫码登录";
            }
        });
    }

    private void OnQrCancelClick(object? sender, RoutedEventArgs e)
    {
        _cloudService.CancelQrLogin();
        if (_qrPanel != null) _qrPanel.IsVisible = false;
        if (_qrButton != null) { _qrButton.IsEnabled = true; _qrButton.Content = "扫码登录"; }
        if (_qrStatusText != null) _qrStatusText.Text = "已取消扫码登录";
    }

    private async void OnRefreshDevicesClick(object? sender, RoutedEventArgs e)
    {
        await RefreshDeviceListAsync();
    }

    private async Task RefreshDeviceListAsync()
    {
        if (!_cloudService.IsLoggedIn) return;

        _deviceCountText!.Text = "正在加载设备列表...";

        try
        {
            var (devices, error) = await _cloudService.GetDeviceListAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (devices != null)
                {
                    _deviceCountText.Text = $"共 {devices.Count} 台设备";
                    _deviceListControl!.ItemsSource = devices;

                    // 构建简单的设备列表显示
                    var stackPanel = new StackPanel { Spacing = 4 };
                    foreach (var device in devices)
                    {
                        var row = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Margin = new Thickness(0, 2)
                        };

                        var dot = new Border
                        {
                            Width = 8,
                            Height = 8,
                            CornerRadius = new CornerRadius(4),
                            Background = device.IsOnline
                                ? Brush.Parse("#4CAF50")
                                : Brush.Parse("#9E9E9E"),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        row.Children.Add(dot);

                        row.Children.Add(new TextBlock
                        {
                            Text = device.Name,
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            Width = 140
                        });

                        row.Children.Add(new TextBlock
                        {
                            Text = device.Model,
                            FontSize = 10,
                            Foreground = Brush.Parse("#999999"),
                            VerticalAlignment = VerticalAlignment.Center,
                            Width = 120
                        });

                        row.Children.Add(new TextBlock
                        {
                            Text = device.IsOnline ? "在线" : "离线",
                            FontSize = 10,
                            Foreground = device.IsOnline
                                ? Brush.Parse("#4CAF50")
                                : Brush.Parse("#9E9E9E"),
                            VerticalAlignment = VerticalAlignment.Center
                        });

                        stackPanel.Children.Add(row);
                    }

                    _deviceListControl.ItemsSource = devices;
                }
                else
                {
                    _deviceCountText.Text = $"✗ {error ?? "加载失败"}";
                }
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _deviceCountText.Text = $"✗ 加载异常: {ex.Message}";
            });
        }
    }

    private void UpdateLoginStatus()
    {
        var loggedIn = _cloudService.IsLoggedIn;
        if (_loginButton != null) _loginButton.IsVisible = !loggedIn;
        if (_logoutButton != null) _logoutButton.IsVisible = loggedIn;
        if (_usernameBox != null) _usernameBox.IsEnabled = !loggedIn;
        if (_passwordBox != null) _passwordBox.IsEnabled = !loggedIn;
        if (_countryCombo != null) _countryCombo.IsEnabled = !loggedIn;
    }

    private void OnPageUnloaded(object? sender, EventArgs e)
    {
        _settings.RefreshIntervalSeconds = (int)(_refreshIntervalBox?.Value ?? 30);
        _settings.Save();
        this.Unloaded -= OnPageUnloaded;
    }
}
