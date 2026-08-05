using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MiIsland.Models;
using MiIsland.Services;

namespace MiIsland.Views;

[SettingsPageInfo("F2A9C1E3-7B4D-4F8A-9C2E-1D3F5A7B9C0E", "MiIsland 设置", "\ue994", "\ue993")]
public partial class SettingsControl : SettingsPageBase
{
    private readonly PluginSettings _settings;
    private readonly MiCloudService _cloudService = MiCloudService.Instance;

    // UI 控件引用
    private Button? _qrButton;
    private Button? _logoutButton;
    private TextBlock? _loginStatusText;
    private TextBlock? _deviceCountText;
    private TextBox? _refreshIntervalBox;
    private ItemsControl? _deviceListControl;
    private List<MiCloudDevice>? _loadedDevices;

    // 快捷方式创建反馈
    private TextBlock? _shortcutHint;

    // 已登录信息卡片
    private Border? _accountInfoCard;
    private TextBlock? _accountLoginTimeText;
    private TextBlock? _accountUserIdText;
    private TextBlock? _accountExpireText;

    // 扫码登录 UI
    private Border? _qrPanel;
    private Image? _qrImage;
    private TextBlock? _qrStatusText;
    private Button? _qrCancelButton;

    public SettingsControl()
    {
        _settings = PluginSettings.Load();
        this.Unloaded += OnPageUnloaded;
        // 宿主构建完成后注册 MiIsland 的 Uri 导航（快捷方式入口）。幂等。
        UriNavBridge.EnsureRegistered();
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        BuildUI();
        UpdateLoginStatus();

        // 已登录则自动加载一次设备列表,避免切回设置页时还停留在「请先登录」占位文案。
        if (_cloudService.IsLoggedIn)
        {
            _deviceCountText!.Text = "正在加载设备列表...";
            _ = RefreshDeviceListAsync();
        }
        else
        {
            _deviceCountText!.Text = "请先登录以获取设备列表";
        }
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

        // === 桌面快捷方式 ===
        rootPanel.Children.Add(BuildShortcutSection());

        // === 合规提示（可见免责声明） ===
        rootPanel.Children.Add(new Border
        {
            BorderBrush = Brush.Parse("#FFE0B2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = Brush.Parse("#FFF8E1"),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 0),
            Child = new TextBlock
            {
                Text = "⚠ 免责声明：本插件为非官方、仅供个人学习研究的第三方工具，仅限非商业使用。使用即表示你已阅读并同意仓库内 DISCLAIMER.md 全部条款。禁止任何形式的商业使用或收费。",
                FontSize = 11,
                Foreground = Brush.Parse("#8D6E63"),
                TextWrapping = TextWrapping.Wrap
            }
        });

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

        // 说明：仅支持扫码登录，不保存任何密码
        section.Children.Add(new TextBlock
        {
            Text = "本插件仅支持「扫码登录」，不收集、不存储任何账号密码。",
            FontSize = 11,
            Foreground = Brush.Parse("#999999"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        });

        // 按钮行：扫码登录 + 登出
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0)
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
        buttonRow.Children.Add(_qrButton);

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

        // 已登录信息卡片（仅登录后可见）
        _accountInfoCard = new Border
        {
            Background = Brush.Parse("#E8F5E9"),
            BorderBrush = Brush.Parse("#A5D6A7"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false
        };
        var infoStack = new StackPanel { Spacing = 3 };

        _accountUserIdText = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush.Parse("#555555")
        };
        infoStack.Children.Add(_accountUserIdText);

        _accountLoginTimeText = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush.Parse("#555555")
        };
        infoStack.Children.Add(_accountLoginTimeText);

        _accountExpireText = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush.Parse("#777777")
        };
        infoStack.Children.Add(_accountExpireText);

        _accountInfoCard.Child = infoStack;
        section.Children.Add(_accountInfoCard);

        // 扫码登录面板（初始隐藏）
        _qrPanel = new Border
        {
            Background = Brush.Parse("#F5F5F5"),
            BorderBrush = Brush.Parse("#E0E0E0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 8, 0, 0),
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
            Text = "",
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
            Margin = new Thickness(0, 4, 0, 0),
            ItemTemplate = new FuncDataTemplate<MiCloudDevice>((device, _) => BuildDeviceRow(device))
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
        _refreshIntervalBox = new TextBox
        {
            Text = _settings.RefreshIntervalSeconds.ToString(),
            Watermark = "0-300",
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

    private StackPanel BuildShortcutSection()
    {
        var section = new StackPanel { Spacing = 6 };

        section.Children.Add(BuildSectionHeader("桌面快捷方式"));

        section.Children.Add(new TextBlock
        {
            Text = "在桌面生成 .lnk 快捷方式，双击即可直达对应页面（需 ClassIsland 正在运行）。系统托盘图标同样直达设备总控页。",
            FontSize = 11,
            Foreground = Brush.Parse("#999999"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        var settingsBtn = new Button
        {
            Content = "设置页快捷方式",
            FontSize = 11,
            Padding = new Thickness(10, 4),
            Background = Brush.Parse("#E0E0E0"),
            CornerRadius = new CornerRadius(3)
        };
        settingsBtn.Click += (_, _) => OnCreateShortcutClick(
            ShortcutHelper.MiIslandShortcutKind.Settings, null, null);
        buttonRow.Children.Add(settingsBtn);

        var controlBtn = new Button
        {
            Content = "设备总控快捷方式",
            FontSize = 11,
            Padding = new Thickness(10, 4),
            Background = Brush.Parse("#E0E0E0"),
            CornerRadius = new CornerRadius(3)
        };
        controlBtn.Click += (_, _) => OnCreateShortcutClick(
            ShortcutHelper.MiIslandShortcutKind.Control, null, null);
        buttonRow.Children.Add(controlBtn);

        section.Children.Add(buttonRow);

        _shortcutHint = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush.Parse("#9E9E9E"),
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 16,
            Margin = new Thickness(0, 4, 0, 0)
        };
        section.Children.Add(_shortcutHint);

        return section;
    }

    private async void OnCreateShortcutClick(ShortcutHelper.MiIslandShortcutKind kind, string? deviceName, string? did)
    {
        // 设备快捷方式：优先用自定义图标，否则尝试云端自动图标（下载并转 ico），
        // 都没有则生成类型占位 .ico（不再回退 MiIsland 图标）
        string? iconPath = null;
        MiDeviceKind deviceKind = MiDeviceKind.Unknown;
        if (kind == ShortcutHelper.MiIslandShortcutKind.Device && !string.IsNullOrEmpty(did))
        {
            var dev = _loadedDevices?.FirstOrDefault(d => d.Did == did);
            deviceKind = dev?.Kind ?? MiDeviceKind.Unknown;

            var custom = _settings.GetDeviceIcon(did);
            if (!string.IsNullOrEmpty(custom))
                iconPath = DeviceImageHelper.EnsureIco(custom);
            else if (dev != null)
            {
                var cloud = await DeviceImageHelper.GetCloudImagePathAsync(dev);
                if (!string.IsNullOrEmpty(cloud))
                    iconPath = DeviceImageHelper.EnsureIco(cloud);
            }

            if (string.IsNullOrEmpty(iconPath))
                iconPath = DeviceImageHelper.EnsurePlaceholderIco(deviceKind, deviceName ?? did);
        }

        // 设置页/总控不传 iconPath → 使用 MiIsland 默认图标
        var path = ShortcutHelper.CreateMiIslandShortcut(kind, deviceName, did, iconPath);
        if (path == null)
        {
            NotifyHint("创建失败：无法写入桌面（请检查权限或 ClassIsland 路径）。", "#F44336");
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        string iconNote = "";
        if (kind == ShortcutHelper.MiIslandShortcutKind.Device && !string.IsNullOrEmpty(iconPath))
        {
            iconNote = iconPath.Contains("ph_", StringComparison.OrdinalIgnoreCase)
                ? "（无设备图，已用类型占位图标）"
                : "（已用设备图标）";
        }
        NotifyHint($"✓ 已在桌面创建：{name}.lnk{iconNote}", "#4CAF50");
    }

    // === 事件处理 ===

    // 账号密码登录已移除：本插件仅支持扫码登录，详见 DISCLAIMER.md。

    private void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        _cloudService.Logout();
        _settings.Account = new MiAccountInfo();
        _settings.Save();

        UpdateLoginStatus();
        _deviceCountText!.Text = "请先登录以获取设备列表";
        _deviceListControl!.ItemsSource = null;
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
                var now = DateTime.Now;
                _settings.Account.Username = "[扫码登录]";
                _settings.Account.LoginTime = now.ToString("yyyy-MM-dd HH:mm:ss");
                _settings.Account.UserId = _cloudService.CurrentUserId ?? "";
                _settings.Account.NickName = _cloudService.CurrentNickName ?? "";
                // 持久化登录 session,下次启动 / 切换页面自动恢复,无需重新扫码
                _settings.Account.ServiceToken = MiCloudService.Obfuscate(_cloudService.CurrentServiceToken);
                _settings.Account.Ssecurity = MiCloudService.Obfuscate(_cloudService.CurrentSsecurity);
                _settings.Account.CUserId = _cloudService.CurrentCUserId ?? "";
                _settings.Account.ExpiresAt = (_cloudService.SessionExpiresAt > DateTime.MinValue
                    ? _cloudService.SessionExpiresAt
                    : now.AddDays(7)).ToString("o");
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
                _loadedDevices = devices;
                _deviceCountText.Text = $"共 {devices.Count} 台设备";
                _deviceListControl!.ItemsSource = devices;
            }
                else
                {
                    _deviceCountText.Text = $"✗ {error ?? "加载失败"}";
                    _deviceListControl!.ItemsSource = null;
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

    /// <summary>单个设备行 (图标 + 名称 + 型号 + 在线状态 + 图标选择/快捷方式按钮)，供 ItemsControl.ItemTemplate 使用</summary>
    private Control BuildDeviceRow(MiCloudDevice device)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 2)
        };

        // 设备图标（自定义优先；其次云端自动图，列表渲染时不做网络下载，仅显示已设置的自定义图）
        var customIcon = _settings.GetDeviceIcon(device.Did);
        if (!string.IsNullOrEmpty(customIcon) && File.Exists(customIcon))
        {
            try
            {
                row.Children.Add(new Image
                {
                    Source = new Bitmap(customIcon),
                    Width = 22, Height = 22,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            catch
            {
                row.Children.Add(DevicePlaceholder(device.Kind));
            }
        }
        else
        {
            // 无自定义图：显示类型占位图（不再用 MiIsland 图标 / 状态点）
            row.Children.Add(DevicePlaceholder(device.Kind));
        }

        row.Children.Add(new TextBlock
        {
            Text = device.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 130
        });

        row.Children.Add(new TextBlock
        {
            Text = device.Model,
            FontSize = 10,
            Foreground = Brush.Parse("#999999"),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 110
        });

        row.Children.Add(new TextBlock
        {
            Text = device.IsOnline ? "在线" : "离线",
            FontSize = 10,
            Foreground = device.IsOnline ? Brush.Parse("#4CAF50") : Brush.Parse("#9E9E9E"),
            VerticalAlignment = VerticalAlignment.Center
        });

        // 选择自定义图标
        var iconBtn = new Button
        {
            Content = "图标",
            FontSize = 10,
            Padding = new Thickness(8, 1),
            Background = Brush.Parse("#EEEEEE"),
            Foreground = Brush.Parse("#555555"),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBtn.Click += async (_, _) => await PickCustomIconAsync(device);
        row.Children.Add(iconBtn);

        // 清除自定义图标（仅当已设置时显示）
        if (!string.IsNullOrEmpty(customIcon))
        {
            var clearBtn = new Button
            {
                Content = "清除图标",
                FontSize = 10,
                Padding = new Thickness(8, 1),
                Background = Brush.Parse("#FFEBEE"),
                Foreground = Brush.Parse("#C62828"),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center
            };
            clearBtn.Click += (_, _) =>
            {
                _settings.ClearDeviceIcon(device.Did);
                _settings.Save();
                NotifyHint($"已清除「{device.Name}」的自定义图标", "#9E9E9E");
                _ = RefreshDeviceListAsync();
            };
            row.Children.Add(clearBtn);
        }

        // 单设备快捷方式按钮
        var shortcutBtn = new Button
        {
            Content = "快捷方式",
            FontSize = 10,
            Padding = new Thickness(8, 1),
            Background = Brush.Parse("#EEEEEE"),
            Foreground = Brush.Parse("#555555"),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center
        };
        shortcutBtn.Click += (_, _) => OnCreateShortcutClick(
            ShortcutHelper.MiIslandShortcutKind.Device, device.Name, device.Did);
        row.Children.Add(shortcutBtn);

        return row;
    }

    private static Border DevicePlaceholder(MiDeviceKind kind) => new()
    {
        Width = 22,
        Height = 22,
        CornerRadius = new CornerRadius(5),
        Background = Brush.Parse(DeviceImageHelper.PlaceholderColor(kind)),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = DeviceImageHelper.PlaceholderGlyph(kind),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    /// <summary>选择自定义设备图标：用 Avalonia StorageProvider 文件选择器，存到设置（按 did）。</summary>
    private async Task PickCustomIconAsync(MiCloudDevice device)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null)
            {
                NotifyHint("无法打开文件选择框（未找到父窗口）", "#F44336");
                return;
            }

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"为「{device.Name}」选择图标",
                AllowMultiple = false,
                FileTypeFilter = new System.Collections.Generic.List<FilePickerFileType>
                {
                    new FilePickerFileType("图片")
                    {
                        Patterns = new System.Collections.Generic.List<string>
                        {
                            "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif"
                        }
                    }
                }
            });
            if (files == null || files.Count == 0) return;

            var path = files[0].Path.LocalPath;
            if (!File.Exists(path)) return;

            _settings.SetDeviceIcon(device.Did, path);
            _settings.Save();
            NotifyHint($"已为「{device.Name}」设置自定义图标", "#4CAF50");
            await RefreshDeviceListAsync();
        }
        catch (Exception ex)
        {
            NotifyHint($"选择图标失败：{ex.Message}", "#F44336");
        }
    }

    private void NotifyHint(string text, string color)
    {
        if (_shortcutHint == null) return;
        _shortcutHint.Text = text;
        _shortcutHint.Foreground = Brush.Parse(color);
    }

    private void UpdateLoginStatus()
    {
        var loggedIn = _cloudService.IsLoggedIn;
        if (_logoutButton != null) _logoutButton.IsVisible = loggedIn;
        if (_qrButton != null) _qrButton.IsEnabled = !loggedIn;

        // 已登录信息卡片
        if (_accountInfoCard == null) return;

        if (loggedIn)
        {
            _accountInfoCard.IsVisible = true;

            if (!string.IsNullOrEmpty(_settings.Account.UserId))
                _accountUserIdText!.Text = $"账号 ID：{_settings.Account.UserId}";
            else
                _accountUserIdText!.Text = "账号 ID：-";

            if (!string.IsNullOrEmpty(_settings.Account.LoginTime))
            {
                _accountLoginTimeText!.Text = $"登录时间：{_settings.Account.LoginTime}";
                // Token 默认 7 天有效期
                if (DateTime.TryParse(_settings.Account.LoginTime, out var loginDt))
                {
                    var expireDt = loginDt.AddDays(7);
                    _accountExpireText!.Text = FormatExpireText(expireDt);
                }
                else
                {
                    _accountExpireText!.Text = "Token 失效时间：-";
                }
            }
            else
            {
                _accountLoginTimeText!.Text = "登录时间：-（本会话未刷新）";
                _accountExpireText!.Text = "Token 失效时间：-";
            }
        }
        else
        {
            _accountInfoCard.IsVisible = false;
            _accountUserIdText!.Text = "";
            _accountLoginTimeText!.Text = "";
            _accountExpireText!.Text = "";
        }
    }

    private void OnPageUnloaded(object? sender, EventArgs e)
    {
        if (_refreshIntervalBox != null &&
            int.TryParse(_refreshIntervalBox.Text, out var value))
        {
            _settings.RefreshIntervalSeconds = Math.Clamp(value, 0, 300);
        }
        _settings.Save();
        this.Unloaded -= OnPageUnloaded;
    }

    private static string FormatExpireText(DateTime expireDt)
    {
        var remaining = expireDt - DateTime.Now;
        var expireText = expireDt.ToString("yyyy-MM-dd HH:mm:ss");
        if (remaining.TotalSeconds <= 0)
            return $"Token 失效时间：{expireText}（已过期）";

        var days = (int)remaining.TotalDays;
        var hours = remaining.Hours;
        var minutes = remaining.Minutes;
        return $"Token 失效时间：{expireText}（约 {days} 天 {hours} 小时 {minutes} 分后）";
    }
}
