using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace YoutubeDownloadHelper.Core.Errors;

public static class ErrorsServiceCollectionExtensions
{
    /// <summary>Registers the <see cref="ErrorClassifier"/> and the <see cref="RetryPolicy"/> (with <see cref="Random.Shared"/>).</summary>
    public static IServiceCollection AddErrors(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ErrorClassifier>();
        services.AddSingleton(sp => new RetryPolicy(sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
