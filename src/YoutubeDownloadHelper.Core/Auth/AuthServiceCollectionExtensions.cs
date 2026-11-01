using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace YoutubeDownloadHelper.Core.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CookieFileAuthStatusProvider"/> as the <see cref="IAuthStatusProvider"/> unless one is
    /// already registered. Needs <c>AddPersistence()</c>.
    /// </summary>
    public static IServiceCollection AddAuth(this IServiceCollection services)
    {
        services.TryAddSingleton<IAuthStatusProvider, CookieFileAuthStatusProvider>();
        return services;
    }
}
