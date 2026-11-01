using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// The login status as <c>GET /info</c> reports it. <see cref="CookieFileAuthStatusProvider"/> is the default; the
/// auth store (#49) replaces it with the verified status.
/// </summary>
public interface IAuthStatusProvider
{
    AuthInfo GetStatus();
}

/// <summary>
/// <see cref="AuthStatus.NotLoggedIn"/> without <c>auth\cookies.bin</c>, else <see cref="AuthStatus.Unknown"/>
/// (nothing verifies the cookies yet).
/// </summary>
public sealed class CookieFileAuthStatusProvider(DataPaths paths) : IAuthStatusProvider
{
    public AuthInfo GetStatus() => new(File.Exists(paths.CookiesBin) ? AuthStatus.Unknown : AuthStatus.NotLoggedIn);
}
