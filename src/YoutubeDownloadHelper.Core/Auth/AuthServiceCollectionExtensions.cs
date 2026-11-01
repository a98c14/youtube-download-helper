using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace YoutubeDownloadHelper.Core.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="CookieJarStore"/>, DPAPI as its <see cref="ISecretProtector"/>, and
    /// <see cref="StoredJarAuthStatusProvider"/> as the <see cref="IAuthStatusProvider"/>, each unless one is already
    /// registered. Needs <c>AddPersistence()</c> and logging.
    /// </summary>
    public static IServiceCollection AddAuth(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISecretProtector>(_ => new DpapiSecretProtector());
        services.TryAddSingleton<CookieJarStore>();
        services.TryAddSingleton<IAuthStatusProvider, StoredJarAuthStatusProvider>();
        return services;
    }
}
