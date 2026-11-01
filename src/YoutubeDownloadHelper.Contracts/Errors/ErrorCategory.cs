namespace YoutubeDownloadHelper.Contracts.Errors;

/// <summary>
/// What kind of failure a yt-dlp run ended with (PLAN.md §5.2); decides the retry action. Persisted by name, so
/// renaming a member is a breaking change for stored items.
/// </summary>
public enum ErrorCategory
{
    /// <summary>Network trouble, HTTP 5xx, 429 or 403, fragment failures: retried with backoff.</summary>
    Transient,

    /// <summary>Members-only, Premium, age or bot check, login needed: waits for valid cookies.</summary>
    Auth,

    /// <summary>Removed, private, copyright, geo-blocked, not found: fails immediately.</summary>
    Unavailable,

    /// <summary>yt-dlp, its JavaScript runtime or ffmpeg is broken or missing: waits for a tool update.</summary>
    ToolBroken,

    /// <summary>
    /// The local file system refused: disk full (<see cref="ErrorCodes.DiskFull"/>, which blocks the queue), access
    /// denied, path too long or missing. The others fail the item.
    /// </summary>
    LocalIo,

    /// <summary>Nothing matched: treated as transient, with fewer retries.</summary>
    Unknown,
}
