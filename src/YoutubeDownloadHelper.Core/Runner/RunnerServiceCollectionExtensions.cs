using Microsoft.Extensions.DependencyInjection;

namespace YoutubeDownloadHelper.Core.Runner;

public static class RunnerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="YtDlpCommandBuilder"/>. Needs <c>AddPersistence()</c>, <c>AddPresets()</c> and
    /// <c>AddTools()</c>.
    /// </summary>
    public static IServiceCollection AddRunner(this IServiceCollection services)
    {
        services.AddSingleton<YtDlpCommandBuilder>();
        return services;
    }
}
