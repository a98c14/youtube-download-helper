namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>Timing of the <see cref="ToolManager"/>'s background loop.</summary>
public sealed record ToolManagerOptions
{
    /// <summary>
    /// How long to wait after a check that left a tool missing (e.g. offline at first start) before trying again; the
    /// last delay repeats. A requested check (a binary vanished, a tool looked broken) does not wait.
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)];

    /// <summary>How often every tool is checked for updates, counted from the last check (PLAN.md §5.6).</summary>
    public TimeSpan UpdateCheckInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>When an update check could not reach the feed, how soon the next one runs.</summary>
    public TimeSpan UpdateRetryInterval { get; init; } = TimeSpan.FromHours(1);
}
