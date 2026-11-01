namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>
/// How a yt-dlp run ended. The runner doesn't judge failures: the scheduler classifies them with
/// <c>ErrorClassifier.Classify(ExitCode, OutputTail)</c> (PLAN.md §5.2). The tail, not just the error lines, because
/// some patterns read other lines (yt-dlp's <c>yt-dlp: error:</c> usage message).
/// </summary>
/// <param name="ExitCode">yt-dlp's exit code (after a cancel, the code the kill gave it).</param>
/// <param name="Cancelled">The run was cancelled (pause, cancel, shutdown) and its process tree killed.</param>
/// <param name="FinalPath">The finished file in the destination, from the <c>moved</c> line; null for other commands.</param>
/// <param name="AlreadyPresent">
/// <c>moved</c> came without any download: yt-dlp found the file already in the destination (spike #33).
/// </param>
/// <param name="ErrorLines">
/// The <c>ERROR:</c> and <c>WARNING:</c> lines of both streams, in order (the last 200), for the friendly message.
/// </param>
/// <param name="OutputTail">
/// The last 200 lines of output, without <c>dl</c> progress lines (PLAN.md §5.4), for the details pane.
/// </param>
/// <param name="LastProgress">The last progress snapshot, whether or not the throttle sent it.</param>
public sealed record YtDlpRunResult(
    int ExitCode,
    bool Cancelled,
    string? FinalPath,
    bool AlreadyPresent,
    IReadOnlyList<string> ErrorLines,
    IReadOnlyList<string> OutputTail,
    DownloadProgress LastProgress)
{
    public bool Succeeded => !Cancelled && ExitCode == 0;
}

/// <summary>A run whose stdout is one JSON document (<c>-J</c>): resolves, flat listings, the auth verification.</summary>
/// <param name="Run">How the run ended; <see cref="YtDlpRunResult.OutputTail"/> holds stderr only.</param>
/// <param name="Json">All of stdout.</param>
public sealed record YtDlpJsonResult(YtDlpRunResult Run, string Json);

/// <summary>A resolve: how the run ended and, when it succeeded with a single video, its metadata.</summary>
public sealed record YtDlpResolveResult(YtDlpRunResult Run, ResolvedVideo? Video);

/// <summary>Per-run options.</summary>
public sealed record YtDlpRunOptions
{
    /// <summary>
    /// The full log (<see cref="Persistence.DataPaths.JobLogFor"/>), appended to, so the resolve and the download of
    /// one attempt share it. Null for no log.
    /// </summary>
    public string? LogFile { get; init; }

    /// <summary>Receives progress snapshots, at most 4 per second (<see cref="ProgressThrottle{T}"/>). Must be quick.</summary>
    public Action<DownloadProgress>? Progress { get; init; }
}
