using YoutubeDownloadHelper.Contracts.Auth;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>The login status as <c>GET /info</c> reports it. <see cref="AuthManager"/> is the engine's: the verified status.</summary>
public interface IAuthStatusProvider
{
    AuthInfo GetStatus();
}
