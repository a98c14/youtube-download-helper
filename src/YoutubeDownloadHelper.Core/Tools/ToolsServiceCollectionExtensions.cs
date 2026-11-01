using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace YoutubeDownloadHelper.Core.Tools;

public static class ToolsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ConfiguredToolLocator"/> as the <see cref="IToolLocator"/> unless one is already
    /// registered, and likewise <see cref="LocatorToolStatusProvider"/> as the <see cref="IToolStatusProvider"/>,
    /// <see cref="AlwaysReadyToolHealth"/> as the <see cref="IToolHealth"/> and <see cref="NoToolUpdateTrigger"/> as the
    /// <see cref="IToolUpdateTrigger"/>. Needs <see cref="Persistence.DataPaths"/> (<c>AddPersistence()</c>).
    /// <see cref="AddToolManager"/> replaces these defaults with the tool manager.
    /// </summary>
    public static IServiceCollection AddTools(this IServiceCollection services, ToolLocatorOptions? options = null)
    {
        services.TryAddSingleton(options ?? new ToolLocatorOptions());
        services.TryAddSingleton<IToolLocator, ConfiguredToolLocator>();
        services.TryAddSingleton<IToolStatusProvider, LocatorToolStatusProvider>();
        services.TryAddSingleton<IToolHealth, AlwaysReadyToolHealth>();
        services.TryAddSingleton<IToolUpdateTrigger, NoToolUpdateTrigger>();
        return services;
    }

    /// <summary>
    /// Registers the <see cref="ToolManager"/> as the <see cref="IToolLocator"/>, <see cref="IToolHealth"/>,
    /// <see cref="IToolStatusProvider"/> and <see cref="IToolUpdateTrigger"/> (replacing the defaults of
    /// <see cref="AddTools"/>), the <see cref="ToolInstaller"/>, the <see cref="ToolManagerOptions"/>, and unless already
    /// registered <see cref="GitHubReleaseFeed"/> as the <see cref="IToolReleaseFeed"/> and
    /// <see cref="ProcessToolSmokeTest"/> as the <see cref="IToolSmokeTest"/>. Needs logging, <c>AddPersistence()</c>,
    /// <c>AddQueue()</c> and <see cref="AddTools"/>. The host calls <see cref="ToolManager.InitializeAsync"/> after the
    /// database is ready and before the scheduler starts, then <see cref="ToolManager.StartAsync"/> and
    /// <see cref="ToolManager.StopAsync"/> with the host.
    /// </summary>
    public static IServiceCollection AddToolManager(this IServiceCollection services, ToolManagerOptions? options = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new ToolManagerOptions());
        services.TryAddSingleton<IToolReleaseFeed>(_ => new GitHubReleaseFeed());
        services.TryAddSingleton<IToolSmokeTest, ProcessToolSmokeTest>();
        services.TryAddSingleton<ToolInstaller>();
        services.TryAddSingleton<ToolManager>();
        services.Replace(ServiceDescriptor.Singleton<IToolLocator>(sp => sp.GetRequiredService<ToolManager>()));
        services.Replace(ServiceDescriptor.Singleton<IToolHealth>(sp => sp.GetRequiredService<ToolManager>()));
        services.Replace(ServiceDescriptor.Singleton<IToolStatusProvider>(sp => sp.GetRequiredService<ToolManager>()));
        services.Replace(ServiceDescriptor.Singleton<IToolUpdateTrigger>(sp => sp.GetRequiredService<ToolManager>()));
        return services;
    }
}
