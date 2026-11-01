using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Core.Queue;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>How a job ended, for <see cref="IJobCookieProvider.AfterJobAsync"/>.</summary>
/// <param name="Succeeded">The item completed or was skipped.</param>
/// <param name="Cancelled">The job was stopped (pause, removal, shutdown) before it ended.</param>
/// <param name="FailureCategory">The classified failure, when the last run failed.</param>
public sealed record JobOutcome(bool Succeeded, bool Cancelled, ErrorCategory? FailureCategory)
{
    public static JobOutcome Stopped { get; } = new(false, true, null);

    public bool FailedWithAuth => FailureCategory == ErrorCategory.Auth;
}

/// <summary>
/// The per-job cookies file (PLAN.md §5.7). The scheduler calls <see cref="BeforeJobAsync"/> before a job's first run
/// and <see cref="AfterJobAsync"/> once the job is over, whatever the outcome; the resolve and the download of one job
/// share the file. <see cref="NoJobCookieProvider"/> is the default (no cookies); the cookie writeback work (#48)
/// replaces it.
/// </summary>
public interface IJobCookieProvider
{
    /// <summary>Writes the job's cookies file and returns its path, or returns <see langword="null"/> to run without cookies.</summary>
    /// <param name="item">The item, as it was when the job started.</param>
    /// <param name="workDir">The item's work folder (<c>data\work\&lt;itemId&gt;\</c>), which may not exist yet.</param>
    /// <param name="cancellationToken">Cancelled when the job is stopped.</param>
    Task<string?> BeforeJobAsync(QueueItem item, string workDir, CancellationToken cancellationToken);

    /// <summary>Reads the cookies yt-dlp wrote back, merges them, and deletes the file. Called with no cancellation.</summary>
    /// <param name="item">The item, as it was when the job started.</param>
    /// <param name="cookiesFile">What <see cref="BeforeJobAsync"/> returned, or null if it was not called or failed.</param>
    /// <param name="outcome">How the job ended.</param>
    /// <param name="cancellationToken">Not cancelled by the scheduler.</param>
    Task AfterJobAsync(QueueItem item, string? cookiesFile, JobOutcome outcome, CancellationToken cancellationToken);
}

/// <summary>Runs every job without cookies.</summary>
public sealed class NoJobCookieProvider : IJobCookieProvider
{
    public Task<string?> BeforeJobAsync(QueueItem item, string workDir, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task AfterJobAsync(QueueItem item, string? cookiesFile, JobOutcome outcome, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
