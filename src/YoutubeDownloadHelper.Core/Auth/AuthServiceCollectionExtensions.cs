using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace YoutubeDownloadHelper.Core.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="CookieJarStore"/>, DPAPI as its <see cref="ISecretProtector"/>, the
    /// <see cref="AuthManager"/>, and <see cref="CookieFileProvider"/> as the <see cref="IJobCookieProvider"/> and the
    /// <see cref="IResolveCookieProvider"/>. Seams for the verification work (#49), each unless one is already
    /// registered: <see cref="StoredJarAuthStatusProvider"/> as the <see cref="IAuthStatusProvider"/> and
    /// <see cref="NoAuthVerificationTrigger"/> as the <see cref="IAuthVerificationTrigger"/>. Needs
    /// <c>AddPersistence()</c>, <c>AddQueue()</c> and logging.
    /// </summary>
    public static IServiceCollection AddAuth(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISecretProtector>(_ => new DpapiSecretProtector());
        services.TryAddSingleton<CookieJarStore>();
        services.TryAddSingleton<IAuthStatusProvider, StoredJarAuthStatusProvider>();
        services.TryAddSingleton<IAuthVerificationTrigger, NoAuthVerificationTrigger>();
        services.TryAddSingleton<AuthManager>();
        services.TryAddSingleton<CookieFileProvider>();
        services.TryAddSingleton<IJobCookieProvider>(sp => sp.GetRequiredService<CookieFileProvider>());
        services.TryAddSingleton<IResolveCookieProvider>(sp => sp.GetRequiredService<CookieFileProvider>());
        return services;
    }
}
