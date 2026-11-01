using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace YoutubeDownloadHelper.Core.Tools;

public static class ToolsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ConfiguredToolLocator"/> as the <see cref="IToolLocator"/> unless one is already
    /// registered. Needs <see cref="Persistence.DataPaths"/> (<c>AddPersistence()</c>).
    /// </summary>
    public static IServiceCollection AddTools(this IServiceCollection services, ToolLocatorOptions? options = null)
    {
        services.TryAddSingleton(options ?? new ToolLocatorOptions());
        services.TryAddSingleton<IToolLocator, ConfiguredToolLocator>();
        return services;
    }
}
