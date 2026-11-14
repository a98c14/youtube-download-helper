namespace YoutubeDownloadHelper.Contracts.Auth;

/// <summary>
/// The body of <c>PUT</c> <see cref="ApiRoutes.AuthCookies"/>: exactly one of <paramref name="Cookies"/> (the login
/// window's cookies) or <paramref name="Netscape"/> (an imported <c>cookies.txt</c>). Only <c>youtube.com</c> cookies are
/// kept (PLAN.md §5.7).
/// </summary>
/// <param name="Cookies">The cookies the login window read from WebView2.</param>
/// <param name="Netscape">The text of a Netscape <c>cookies.txt</c> file.</param>
public sealed record SetCookiesRequest(IReadOnlyList<CookieInfo>? Cookies = null, string? Netscape = null);

/// <summary>One browser cookie.</summary>
/// <param name="Domain">The cookie's domain. A leading dot makes it a domain cookie (sent to subdomains); without one it is host-only.</param>
/// <param name="Path">The cookie's path, e.g. <c>/</c>.</param>
/// <param name="Name">The cookie's name.</param>
/// <param name="Value">The cookie's value. No tabs or line breaks.</param>
/// <param name="Secure">Sent over HTTPS only.</param>
/// <param name="HttpOnly">Hidden from page scripts.</param>
/// <param name="Expires">When it expires; null for a session cookie.</param>
public sealed record CookieInfo(
    string Domain,
    string Path,
    string Name,
    string Value,
    bool Secure = false,
    bool HttpOnly = false,
    DateTimeOffset? Expires = null);

/// <summary>The answer to <c>PUT</c> <see cref="ApiRoutes.AuthCookies"/>.</summary>
/// <param name="Auth">The login status with the new cookies.</param>
/// <param name="Stored">How many cookies were stored (the <c>youtube.com</c> ones).</param>
/// <param name="SkippedLines">For <see cref="SetCookiesRequest.Netscape"/>: the 1-based numbers of the lines that were not valid cookies.</param>
public sealed record SetCookiesResponse(AuthInfo Auth, int Stored, IReadOnlyList<int> SkippedLines);

/// <summary>The data of the <see cref="Events.EventTypes.AuthChanged"/> event.</summary>
/// <param name="Auth">The login status as it is now.</param>
public sealed record AuthChangedData(AuthInfo Auth);

/// <summary>Message codes of the auth endpoints' error bodies.</summary>
public static class AuthCodes
{
    /// <summary>422: the cookies hold no <c>youtube.com</c> cookie, so there is nothing to store.</summary>
    public const string NoYouTubeCookies = "auth.noYouTubeCookies";

    /// <summary>409 from <see cref="ApiRoutes.AuthRefreshBegin"/>: no cookies are stored, so there is nothing to refresh.</summary>
    public const string NotLoggedIn = "auth.notLoggedIn";

    /// <summary>409 from <see cref="ApiRoutes.AuthRefreshBegin"/>: another browser refresh holds the lease.</summary>
    public const string RefreshInProgress = "auth.refresh.inProgress";

    /// <summary>409 from <see cref="ApiRoutes.AuthRefreshComplete"/>: the lease is unknown, already completed or expired.</summary>
    public const string RefreshNoLease = "auth.refresh.noLease";

    /// <summary>
    /// 409 from <see cref="ApiRoutes.AuthRefreshComplete"/>: the user logged in, imported cookies or logged out since the
    /// refresh began, so its cookies belong to a session that is gone.
    /// </summary>
    public const string RefreshSuperseded = "auth.refresh.superseded";

    /// <summary>422 from <see cref="ApiRoutes.AuthRefreshComplete"/>: the new cookies failed verification; the stored ones stay.</summary>
    public const string RefreshRejected = "auth.refresh.rejected";

    /// <summary>
    /// 422 from <see cref="ApiRoutes.AuthRefreshComplete"/>: the new cookies could not be verified (offline, tools missing);
    /// the stored ones stay.
    /// </summary>
    public const string RefreshUnverified = "auth.refresh.unverified";
}
