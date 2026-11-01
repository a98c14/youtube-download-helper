namespace YoutubeDownloadHelper.Core.Scheduling;

/// <summary>
/// The scheduler's timings and thresholds (PLAN.md §4.3). The defaults are the product's; tests shorten or pin them.
/// The number of parallel jobs is the <c>Concurrency</c> setting, not an option.
/// </summary>
public sealed record SchedulerOptions
{
    /// <summary>The shortest random pause between two consecutive YouTube job starts.</summary>
    public TimeSpan MinYouTubeStartDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest random pause between two consecutive YouTube job starts.</summary>
    public TimeSpan MaxYouTubeStartDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The longest the scheduler waits before it looks at the queue again, with nothing else to wake it.</summary>
    public TimeSpan MaxIdleWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Network failures in a row (no success or other failure between them) that raise <c>WaitingForNetwork</c>.</summary>
    public int NetworkFailureThreshold { get; init; } = 3;

    /// <summary>How often connectivity is probed while <c>WaitingForNetwork</c> is raised.</summary>
    public TimeSpan NetworkProbeInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Free space a job needs on top of its size estimate, on the work folder's and the destination's drives.</summary>
    public long DiskMarginBytes { get; init; } = 500L << 20;

    /// <summary>How often free space is checked again while <c>DiskFull</c> is raised.</summary>
    public TimeSpan DiskRecheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The least time between two yt-dlp update checks the scheduler asks for (PLAN.md §5.2).</summary>
    public TimeSpan ToolUpdateRequestInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Picks the YouTube start delays. <see cref="System.Random.Shared"/> unless a test pins it.</summary>
    public Random Random { get; init; } = Random.Shared;
}
