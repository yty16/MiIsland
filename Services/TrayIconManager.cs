using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Avalonia.Threading;
using MiIsland.Views;

namespace MiIsland.Services;

/// <summary>
/// 系统托盘图标管理 (单例)。
/// 用 WinForms NotifyIcon 显示 MiIsland 托盘图标，点击打开独立控制窗口。
/// 注意：NotifyIcon 必须在 UI 线程创建（其内部窗口依赖线程消息泵）。
/// </summary>
public class TrayIconManager
{
    private static readonly TrayIconManager _instance = new();
    public static TrayIconManager Instance => _instance;

    private NotifyIcon? _notifyIcon;
    private Icon? _icon;
    private MiIslandControlWindow? _window;

    public void Start()
    {
        if (_notifyIcon != null) return;

        _icon = LoadIcon();
        _notifyIcon = new NotifyIcon
        {
            Text = "MiIsland 米家设备控制",
            Icon = _icon,
            Visible = true
        };

        _notifyIcon.Click += (_, _) => ShowWindow();
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("打开控制页");
        openItem.Click += (_, _) => ShowWindow();
        var exitItem = new ToolStripMenuItem("退出托盘");
        exitItem.Click += (_, _) => Stop();
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = menu;
    }

    private void ShowWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_window == null)
            {
                _window = new MiIslandControlWindow();
                _window.Closed += (_, _) => { _window = null; };
            }
            _window.Show();
            _window.Activate();
        });
    }

    public void Stop()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _window?.RequestClose();
            _window = null;
        });

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        if (_icon != null)
        {
            DestroyIcon(_icon.Handle);
            _icon.Dispose();
            _icon = null;
        }
    }

    /// <summary>
    /// 从插件目录的 icon.png 生成托盘图标；失败时退化为纯色占位图标。
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(TrayIconManager).Assembly.Location);
            if (dir != null)
            {
                var png = Path.Combine(dir, "icon.png");
                if (File.Exists(png))
                {
                    using var bmp = new Bitmap(png);
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }
        }
        catch
        {
            // 忽略, 使用降级图标
        }

        using var fallback = new Bitmap(16, 16);
        using var g = Graphics.FromImage(fallback);
        g.Clear(System.Drawing.Color.SteelBlue);
        return Icon.FromHandle(fallback.GetHicon());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
