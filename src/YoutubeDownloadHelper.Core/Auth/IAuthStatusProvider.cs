using YoutubeDownloadHelper.Contracts.Auth;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// The login status as <c>GET /info</c> reports it. <see cref="StoredJarAuthStatusProvider"/> is the default; the
/// verification work (#49) replaces it with the verified status.
/// </summary>
public interface IAuthStatusProvider
{
    AuthInfo GetStatus();
}

/// <summary>
/// <see cref="AuthStatus.NotLoggedIn"/> when the <see cref="CookieJarStore"/> holds no jar, else
/// <see cref="AuthStatus.Unknown"/> (nothing verifies the cookies yet).
/// </summary>
public sealed class StoredJarAuthStatusProvider(CookieJarStore store) : IAuthStatusProvider
{
    public AuthInfo GetStatus() => new(store.Current is null ? AuthStatus.NotLoggedIn : AuthStatus.Unknown);
}
