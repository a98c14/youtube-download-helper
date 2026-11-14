using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace YoutubeDownloadHelper.Core.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="CookieJarStore"/>, DPAPI as its <see cref="ISecretProtector"/>, the
    /// <see cref="AuthManager"/> (also the <see cref="IAuthStatusProvider"/>), <see cref="CookieFileProvider"/> as the
    /// <see cref="IJobCookieProvider"/> and the <see cref="IResolveCookieProvider"/>, the <see cref="AuthVerifier"/> with
    /// <see cref="AuthVerificationRequests"/> as the <see cref="IAuthVerificationTrigger"/>, the
    /// <see cref="BrowserRefreshLease"/> (also the <see cref="IJobStartHold"/>) and the <see cref="AuthOptions"/>. Unless
    /// already registered: <see cref="YtDlpAuthProbe"/> as the <see cref="IAuthProbe"/> and <see cref="NoUiClientPresence"/>
    /// as the <see cref="IUiClientPresence"/> (the engine host registers its event stream). Needs logging,
    /// <c>AddPersistence()</c>, <c>AddQueue()</c>, <c>AddErrors()</c>, <c>AddRunner()</c> and <c>AddTools()</c>. The host
    /// calls <see cref="AuthVerifier.StartAsync"/> after the queue actor has started, and <see cref="AuthVerifier.StopAsync"/>
    /// when it stops.
    /// </summary>
    public static IServiceCollection AddAuth(this IServiceCollection services, AuthOptions? options = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new AuthOptions());
        services.TryAddSingleton<ISecretProtector>(_ => new DpapiSecretProtector());
        services.TryAddSingleton<CookieJarStore>();
        services.TryAddSingleton<AuthVerificationRequests>();
        services.TryAddSingleton<IAuthVerificationTrigger>(sp => sp.GetRequiredService<AuthVerificationRequests>());
        services.TryAddSingleton<AuthManager>();
        services.TryAddSingleton<IAuthStatusProvider>(sp => sp.GetRequiredService<AuthManager>());
        services.TryAddSingleton<IAuthProbe, YtDlpAuthProbe>();
        services.TryAddSingleton<IUiClientPresence, NoUiClientPresence>();
        services.TryAddSingleton<BrowserRefreshLease>();
        services.TryAddSingleton<IJobStartHold>(sp => sp.GetRequiredService<BrowserRefreshLease>());
        services.TryAddSingleton<AuthVerifier>();
        services.TryAddSingleton<CookieFileProvider>();
        services.TryAddSingleton<IJobCookieProvider>(sp => sp.GetRequiredService<CookieFileProvider>());
        services.TryAddSingleton<IResolveCookieProvider>(sp => sp.GetRequiredService<CookieFileProvider>());
        return services;
    }
}
