namespace YoutubeDownloadHelper.Contracts.Auth;

/// <summary>The YouTube login.</summary>
/// <param name="Status">The login status.</param>
/// <param name="Account">The account name, when it is known.</param>
public sealed record AuthInfo(AuthStatus Status, string? Account = null);
