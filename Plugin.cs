using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MiIsland.Services;
using MiIsland.Views;

namespace MiIsland;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 米家云服务使用全局共享单例 MiCloudService.Instance（设置页与组件共用同一会话）。
        // 注册主界面组件
        services.AddComponent<MiHomeComponent>();
        
        // 注册设置页面
        services.AddSettingsPage<SettingsControl>();
    }
}
