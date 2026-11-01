namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>Where a cookie generation came from (PLAN.md §5.7 point 6).</summary>
public enum CookieSource
{
    /// <summary>The embedded WebView2 login.</summary>
    Login,

    /// <summary>A <c>cookies.txt</c> import.</summary>
    Import,

    /// <summary>A yt-dlp job's writeback, merged onto the current jar.</summary>
    Writeback,

    /// <summary>The keep-alive ping's writeback, merged like a job's.</summary>
    Ping,

    /// <summary>A hidden-browser refresh, which rotates <c>*PSIDTS</c>.</summary>
    BrowserRefresh,

    /// <summary>A copy of the generation a browser refresh started from.</summary>
    Rollback,
}

/// <summary>
/// One stored version of the cookie jar.
/// </summary>
/// <param name="Number">Increases by one with every generation; never reused until the store is cleared.</param>
/// <param name="Source">What produced it.</param>
/// <param name="CreatedAt">When it was stored.</param>
/// <param name="BasedOn">The generation it was derived from: the jar a writeback was merged onto, the jar a refresh
/// started from, the generation a rollback restored. <see langword="null"/> for a login or import.</param>
/// <param name="RollbackTo">
/// The generation a rollback would restore: the one the latest browser refresh in this lineage started from.
/// Writebacks and pings never rotate <c>*PSIDTS</c>, so they inherit it; a login, import or rollback has none. The
/// store always keeps this generation while it is current's target.
/// </param>
/// <param name="KnownGoodAt">When it was last confirmed to work (verification passed), if ever.</param>
/// <param name="Jar">The cookies.</param>
public sealed record CookieGeneration(
    long Number,
    CookieSource Source,
    DateTimeOffset CreatedAt,
    long? BasedOn,
    long? RollbackTo,
    DateTimeOffset? KnownGoodAt,
    CookieJar Jar)
{
    public bool IsKnownGood => KnownGoodAt is not null;
}
