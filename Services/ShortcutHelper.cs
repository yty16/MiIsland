using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MiIsland.Services;

namespace MiIsland.Services;

/// <summary>
/// 桌面快捷方式创建工具。
/// 通过 IShellLink COM 接口生成 .lnk 文件，目标指向当前运行的 ClassIsland 宿主进程，
/// 参数为 --uri classisland://plugins/MiIsland/...。ClassIsland 发现已有实例时会把该 Uri
/// 通过 IPC 转发给正在运行的实例，从而触发本插件注册的导航处理（打开对应页面/窗口）。
/// 注意：依赖 WinForms（Microsoft.WindowsDesktop.App），与托盘图标同源。
/// </summary>
public static class ShortcutHelper
{
    // === COM 互操作：IShellLink + IPersistFile ===

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIcon, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIcon, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    // === 公共 API ===

    public enum MiIslandShortcutKind
    {
        Settings,
        Control,
        Device
    }

    /// <summary>
    /// 在桌面创建指向指定 MiIsland 页面的快捷方式。
    /// 成功返回快捷方式完整路径；失败/不支持时返回 null。
    /// </summary>
    public static string? CreateMiIslandShortcut(MiIslandShortcutKind kind, string? deviceName, string? did)
    {
        try
        {
            var host = HostExePath;
            if (string.IsNullOrEmpty(host)) return null;

            var uri = kind switch
            {
                MiIslandShortcutKind.Settings => "classisland://plugins/MiIsland/settings",
                MiIslandShortcutKind.Control => "classisland://plugins/MiIsland/control",
                MiIslandShortcutKind.Device => $"classisland://plugins/MiIsland/device/{did}",
                _ => null
            };
            if (uri == null) return null;

            var label = kind switch
            {
                MiIslandShortcutKind.Settings => "MiIsland 设置",
                MiIslandShortcutKind.Control => "MiIsland 设备总控",
                MiIslandShortcutKind.Device => $"MiIsland {(string.IsNullOrWhiteSpace(deviceName) ? did : deviceName)}",
                _ => "MiIsland"
            };

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var safeName = SanitizeFileName(label);
            var lnkPath = Path.Combine(desktop, safeName + ".lnk");

            // 避免覆盖已有同名快捷方式，递增编号
            var i = 1;
            while (File.Exists(lnkPath))
            {
                lnkPath = Path.Combine(desktop, $"{safeName} ({i}).lnk");
                i++;
            }

            var icon = GetShortcutIcon() ?? host;
            CreateShortcut(lnkPath, host, $"--uri \"{uri}\"",
                Path.GetDirectoryName(host), label, icon, 0);
            return lnkPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiIsland] shortcut error: {ex}");
            return null;
        }
    }

    // === 内部实现 ===

    private static string? HostExePath =>
        Environment.ProcessPath
        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;

    private static string PluginDir =>
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
        ?? AppContext.BaseDirectory;

    private static string? _cachedIco;

    /// <summary>把插件 icon.png 转成 48x48 的 .ico 缓存到插件目录，供 .lnk 使用；失败返回 null。</summary>
    private static string? GetShortcutIcon()
    {
        try
        {
            if (_cachedIco != null && File.Exists(_cachedIco)) return _cachedIco;
            var png = Path.Combine(PluginDir, "icon.png");
            if (!File.Exists(png)) return null;

            var ico = Path.Combine(PluginDir, "shortcut.ico");
            if (!File.Exists(ico))
            {
                using var src = new Bitmap(png);
                using var bmp = new Bitmap(48, 48);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, 0, 0, 48, 48);
                }

                using var icon = Icon.FromHandle(bmp.GetHicon());
                using var fs = File.Create(ico);
                icon.Save(fs);
            }

            _cachedIco = ico;
            return ico;
        }
        catch
        {
            return null;
        }
    }

    private static void CreateShortcut(string lnkPath, string targetPath, string arguments,
        string? workingDir, string? description, string? iconPath, int iconIndex)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetPath);
        if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
        if (!string.IsNullOrEmpty(workingDir)) link.SetWorkingDirectory(workingDir);
        if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
        if (!string.IsNullOrEmpty(iconPath)) link.SetIconLocation(iconPath, iconIndex);

        var persist = (IPersistFile)link;
        persist.Save(lnkPath, true);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim();
        return string.IsNullOrEmpty(s) ? "MiIsland" : s;
    }
}
