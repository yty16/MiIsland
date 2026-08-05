using System;
using System.Threading;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.UriNavigation;
using ClassIsland.Shared;
using MiIsland.Views;

namespace MiIsland.Services;

/// <summary>
/// MiIsland 的 classisland://plugins/MiIsland/... Uri 导航注册桥。
/// 通过 IAppHost.GetService 获取 IUriNavigationService（宿主已构建后，在组件/设置页构造时调用本方法）。
/// 注册的处理程序会在 UI 线程打开对应的设置页 / 设备总控窗口 / 单个设备窗口。
/// 幂等：无论被组件还是设置页调用，只注册一次。
/// </summary>
public static class UriNavBridge
{
    private static int _registered;

    /// <summary>确保已注册 MiIsland 的 Uri 导航处理程序（线程安全、幂等；失败可重试）。</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.CompareExchange(ref _registered, 1, 0) == 1) return; // 已完成注册
        try
        {
            var nav = IAppHost.GetService<IUriNavigationService>();
            RegisterHandlers(nav);
            // 注册成功：保持 _registered = 1，避免重复注册导致窗口被打开多次。
        }
        catch (Exception ex)
        {
            // 宿主尚未就绪或服务暂不可用：复位标记，等待下一次调用（如打开设置页）重试。
            _registered = 0;
            System.Diagnostics.Debug.WriteLine($"[MiIsland] URI 导航注册失败（将重试）: {ex.Message}");
        }
    }

    private static void RegisterHandlers(IUriNavigationService nav)
    {
        const string prefix = "MiIsland";
        nav.HandlePluginsNavigation($"{prefix}/settings", _ => OpenSettings(nav));
        nav.HandlePluginsNavigation($"{prefix}/control", _ => OpenControl());
        nav.HandlePluginsNavigation($"{prefix}/device", args => OpenDevice(args));
    }

    private static void OpenSettings(IUriNavigationService nav)
    {
        // 设置页的导航 Id 即 SettingsPageInfo 上声明的 Guid
        Dispatcher.UIThread.Post(() => RunWithRetry(() =>
            nav.NavigateWrapped(
                new Uri("classisland://app/settings/F2A9C1E3-7B4D-4F8A-9C2E-1D3F5A7B9C0E"), out _)));
    }

    private static void OpenControl()
    {
        Dispatcher.UIThread.Post(() => RunWithRetry(MiIslandControlWindow.ShowControlWindow));
    }

    private static void OpenDevice(UriNavigationEventArgs args)
    {
        var did = args.ChildrenPathPatterns.Count > 0 ? args.ChildrenPathPatterns[0] : "";
        Dispatcher.UIThread.Post(() => RunWithRetry(() => MiIslandDeviceWindow.ShowDeviceWindow(did)));
    }

    /// <summary>在 UI 线程执行打开动作；若宿主尚未就绪（罕见竞态），400ms 后重试一次。</summary>
    private static void RunWithRetry(Action open)
    {
        try
        {
            open();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MiIsland] Uri 处理首次失败，400ms 后重试: {ex.Message}");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { open(); }
                catch (Exception ex2)
                {
                    System.Diagnostics.Debug.WriteLine($"[MiIsland] Uri 处理重试仍失败: {ex2.Message}");
                }
            };
            timer.Start();
        }
    }
}
