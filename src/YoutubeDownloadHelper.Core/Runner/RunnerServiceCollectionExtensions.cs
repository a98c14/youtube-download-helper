using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace YoutubeDownloadHelper.Core.Runner;

public static class RunnerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="YtDlpCommandBuilder"/> and the <see cref="YtDlpRunner"/>. Needs logging,
    /// <c>AddPersistence()</c>, <c>AddPresets()</c> and <c>AddTools()</c>.
    /// </summary>
    public static IServiceCollection AddRunner(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<YtDlpCommandBuilder>();
        services.AddSingleton<YtDlpRunner>();
        return services;
    }
}
