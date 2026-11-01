using Microsoft.Extensions.DependencyInjection;

namespace YoutubeDownloadHelper.Core.Resolve;

public static class ResolveServiceCollectionExtensions
{
    /// <summary>Registers the <see cref="UrlResolver"/>. Needs logging, <c>AddErrors()</c>, <c>AddRunner()</c>, <c>AddTools()</c> and <c>AddAuth()</c>.</summary>
    public static IServiceCollection AddResolve(this IServiceCollection services)
    {
        services.AddSingleton<UrlResolver>();
        return services;
    }
}
