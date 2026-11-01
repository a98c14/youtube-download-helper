using Microsoft.Extensions.DependencyInjection;

namespace YoutubeDownloadHelper.Core.Presets;

public static class PresetsServiceCollectionExtensions
{
    /// <summary>Registers the <see cref="PresetRegistry"/>.</summary>
    public static IServiceCollection AddPresets(this IServiceCollection services)
    {
        services.AddSingleton<PresetRegistry>();
        return services;
    }
}
