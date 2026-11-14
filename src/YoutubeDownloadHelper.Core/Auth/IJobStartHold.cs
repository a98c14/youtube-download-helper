namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// Holds new job starts while the cookie lineage is moving: a browser refresh holds new cookie-using job starts until it
/// completes (PLAN.md §5.7 point 4). The scheduler asks before each pass that would start jobs, and looks again when the
/// hold ends or an event arrives. <see cref="BrowserRefreshLease"/> is the engine's; <see cref="NoJobStartHold"/> never
/// holds.
/// </summary>
public interface IJobStartHold
{
    /// <summary>When the current hold ends at the latest, or <see langword="null"/> when jobs may start now.</summary>
    DateTimeOffset? HeldUntil { get; }
}

/// <summary>Never holds a start.</summary>
public sealed class NoJobStartHold : IJobStartHold
{
    public DateTimeOffset? HeldUntil => null;
}
