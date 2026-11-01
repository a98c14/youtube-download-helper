namespace YoutubeDownloadHelper.Contracts.Errors;

/// <summary>
/// Stable codes of the messages a failed yt-dlp run produces (PLAN.md §5.2, §6.3); clients localize from them.
/// <c>error.&lt;category&gt;.&lt;reason&gt;</c>. The args each code carries are listed on it.
/// </summary>
public static class ErrorCodes
{
    // Transient

    /// <summary>HTTP 429 or YouTube's "try again later". No args.</summary>
    public const string RateLimited = "error.transient.rateLimited";

    /// <summary>HTTP 5xx. Args: <c>status</c>.</summary>
    public const string ServerError = "error.transient.serverError";

    /// <summary>HTTP 403, usually an expired or throttled media URL. No args.</summary>
    public const string Forbidden = "error.transient.forbidden";

    /// <summary>DNS, connect, reset, timeout or TLS failures. No args.</summary>
    public const string Network = "error.transient.network";

    /// <summary>A fragment of an HLS/DASH download could not be fetched. No args.</summary>
    public const string Fragment = "error.transient.fragment";

    // Auth

    public const string MembersOnly = "error.auth.membersOnly";
    public const string PremiumOnly = "error.auth.premiumOnly";
    public const string AgeRestricted = "error.auth.ageRestricted";
    public const string BotCheck = "error.auth.botCheck";

    /// <summary>The content needs a login, or the cookies are no longer valid. No args.</summary>
    public const string LoginRequired = "error.auth.loginRequired";

    // Unavailable

    public const string Private = "error.unavailable.private";

    /// <summary>Removed, unavailable, or the uploader's account was terminated. No args.</summary>
    public const string Removed = "error.unavailable.removed";

    public const string Copyright = "error.unavailable.copyright";
    public const string GeoBlocked = "error.unavailable.geoBlocked";

    /// <summary>An upcoming live stream or premiere (PLAN.md §5.1). No args.</summary>
    public const string Upcoming = "error.unavailable.upcoming";

    /// <summary>HTTP 404. No args.</summary>
    public const string NotFound = "error.unavailable.notFound";

    public const string PlaylistNotFound = "error.unavailable.playlistNotFound";

    /// <summary>yt-dlp has no extractor for the URL. Args: <c>url</c>.</summary>
    public const string UnsupportedUrl = "error.unavailable.unsupportedUrl";

    // ToolBroken

    /// <summary>yt-dlp could not parse YouTube's pages ("Unable to extract …"). No args.</summary>
    public const string Extractor = "error.toolBroken.extractor";

    /// <summary>Signature or n-challenge solving failed. No args.</summary>
    public const string Signature = "error.toolBroken.signature";

    public const string JsRuntimeMissing = "error.toolBroken.jsRuntimeMissing";
    public const string FfmpegMissing = "error.toolBroken.ffmpegMissing";

    /// <summary>yt-dlp rejected the command line (exit code 2). No args.</summary>
    public const string BadOptions = "error.toolBroken.badOptions";

    /// <summary>yt-dlp.exe crashed or could not start (a Windows status exit code). Args: <c>exitCode</c> (hex).</summary>
    public const string Crashed = "error.toolBroken.crashed";

    // LocalIo. Args: path, when yt-dlp names one.

    /// <summary>No args. Blocks the queue instead of failing the item.</summary>
    public const string DiskFull = "error.localIo.diskFull";

    public const string AccessDenied = "error.localIo.accessDenied";
    public const string FileInUse = "error.localIo.fileInUse";

    /// <summary>The path or one of its names is too long (or otherwise invalid).</summary>
    public const string PathTooLong = "error.localIo.pathTooLong";

    public const string PathNotFound = "error.localIo.pathNotFound";

    // Unknown

    /// <summary>Args: <c>exitCode</c>.</summary>
    public const string Unknown = "error.unknown";
}
