using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace YoutubeDownloadHelper.Core.Tools;

public static class ToolsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ConfiguredToolLocator"/> as the <see cref="IToolLocator"/> unless one is already
    /// registered, and likewise <see cref="LocatorToolStatusProvider"/> as the <see cref="IToolStatusProvider"/>,
    /// <see cref="AlwaysReadyToolHealth"/> as the <see cref="IToolHealth"/> and <see cref="NoToolUpdateTrigger"/> as the
    /// <see cref="IToolUpdateTrigger"/>. Needs
    /// <see cref="Persistence.DataPaths"/> (<c>AddPersistence()</c>).
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
}
