namespace YoutubeDownloadHelper.Contracts.Auth;

/// <summary>The YouTube login status (PLAN.md §5.7).</summary>
public enum AuthStatus
{
    /// <summary>Cookies are stored but have not been verified yet.</summary>
    Unknown,

    NotLoggedIn,
    Valid,

    /// <summary>The login is gone; the user has to log in again.</summary>
    Expired,
}
