namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>Where a download run is, from the <c>YDH1</c> lines (spike #33, "Detecting the phase").</summary>
public enum DownloadPhase
{
    /// <summary>Started; no download or post-processing line yet (extraction, format selection).</summary>
    Preparing,

    /// <summary>From the first <c>dl</c> line until post-processing starts.</summary>
    Downloading,

    /// <summary>
    /// A post-processor other than <c>MoveFiles</c> is running (merge, conversion, embeds). There is no percentage, so
    /// <see cref="DownloadProgress.Postprocessor"/> names it instead.
    /// </summary>
    Processing,

    /// <summary><c>MoveFiles</c>: the file is going from the work folder to the destination.</summary>
    Moving,

    /// <summary>The <c>moved</c> line arrived: the file is in the destination.</summary>
    Done,
}

/// <summary>
/// One progress snapshot of a download run. Kept in memory only (PLAN.md §4.3); the runner coalesces it to at most 4
/// per second. Every number is optional because yt-dlp omits fields it doesn't know.
/// </summary>
/// <param name="Phase">The current phase.</param>
/// <param name="DownloadedBytes">Bytes downloaded so far over all formats.</param>
/// <param name="TotalBytes">The expected total over all formats: exact, an estimate, or unknown.</param>
/// <param name="BytesPerSecond">The current download speed.</param>
/// <param name="Eta">Time left for the download (not post-processing).</param>
/// <param name="Fraction">Download progress from 0 to 1, or <see langword="null"/> when unknown (e.g. a live stream).</param>
/// <param name="Postprocessor">The running post-processor in <see cref="DownloadPhase.Processing"/> and <see cref="DownloadPhase.Moving"/>.</param>
public sealed record DownloadProgress(
    DownloadPhase Phase,
    long? DownloadedBytes,
    long? TotalBytes,
    double? BytesPerSecond,
    TimeSpan? Eta,
    double? Fraction,
    string? Postprocessor)
{
    public static DownloadProgress Preparing { get; } = new(DownloadPhase.Preparing, null, null, null, null, null, null);
}
