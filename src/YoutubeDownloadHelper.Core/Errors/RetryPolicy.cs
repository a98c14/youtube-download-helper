using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Core.Queue;

namespace YoutubeDownloadHelper.Core.Errors;

/// <summary>What to do with an item whose job failed.</summary>
public enum RetryAction
{
    /// <summary><see cref="ItemTrigger.WaitForRetry"/> until <see cref="RetryDecision.NextAttemptAt"/>.</summary>
    WaitForRetry,

    /// <summary><see cref="ItemTrigger.WaitForAuth"/>; the caller triggers an auth verification.</summary>
    WaitForAuth,

    /// <summary>
    /// <see cref="ItemTrigger.WaitForToolFix"/>; the caller forces a yt-dlp update check. The item fails if it is still
    /// waiting at <see cref="RetryDecision.NextAttemptAt"/> (the deadline).
    /// </summary>
    WaitForToolFix,

    /// <summary><see cref="ItemTrigger.Fail"/>: a permanent error, or retries exhausted.</summary>
    Fail,

    /// <summary>
    /// The disk is full: the item is not failed. The caller interrupts it (back to <see cref="Contracts.Queue.ItemState.Queued"/>)
    /// and raises the <c>DiskFull</c> queue block (PLAN.md §4.3).
    /// </summary>
    BlockDiskFull,
}

public sealed record RetryDecision(RetryAction Action, DateTimeOffset? NextAttemptAt = null)
{
    /// <summary>The <see cref="JobFailure"/> for <see cref="QueueActor.FailAsync"/>, or null for <see cref="RetryAction.BlockDiskFull"/>.</summary>
    public JobFailure? ToJobFailure(ErrorClassification error, JobOutput? output = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        ItemTrigger? trigger = Action switch
        {
            RetryAction.WaitForRetry => ItemTrigger.WaitForRetry,
            RetryAction.WaitForAuth => ItemTrigger.WaitForAuth,
            RetryAction.WaitForToolFix => ItemTrigger.WaitForToolFix,
            RetryAction.Fail => ItemTrigger.Fail,
            _ => null,
        };
        return trigger is { } t ? new JobFailure(t, error.Category, error.Error, NextAttemptAt, output) : null;
    }
}

/// <summary>
/// Decides what a failure leads to (PLAN.md §5.2). Transient errors back off 30 s → 2 m → 10 m → 30 m with ±20 %
/// jitter and then fail; unknown errors are treated as transient but retried at most twice; auth errors wait for
/// cookies; broken tools wait up to 24 h for an update; unavailable videos and local I/O errors fail at once, except
/// disk full, which blocks the queue.
/// </summary>
public sealed class RetryPolicy(TimeProvider timeProvider, Random? random = null)
{
    /// <summary>The base delay before retry n + 1, where n is the item's retry count so far.</summary>
    public static IReadOnlyList<TimeSpan> Backoff { get; } =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    /// <summary>Each delay is scaled by a uniform random factor between 1 − Jitter and 1 + Jitter.</summary>
    public const double Jitter = 0.2;

    public const int UnknownMaxRetries = 2;

    /// <summary>How long an item waits in <see cref="Contracts.Queue.ItemState.WaitingToolFix"/> for an update before it fails.</summary>
    public static TimeSpan ToolFixTimeout { get; } = TimeSpan.FromHours(24);

    private readonly Random _random = random ?? Random.Shared;

    /// <param name="error">The classified failure.</param>
    /// <param name="attempt">The item's retry count so far (<see cref="QueueItem.Attempt"/>).</param>
    public RetryDecision Decide(ErrorClassification error, int attempt)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        return error.Category switch
        {
            ErrorCategory.Transient => WaitOrFail(attempt, Backoff.Count),
            ErrorCategory.Unknown => WaitOrFail(attempt, UnknownMaxRetries),
            ErrorCategory.Auth => new(RetryAction.WaitForAuth),
            ErrorCategory.ToolBroken => new(RetryAction.WaitForToolFix, timeProvider.GetUtcNow() + ToolFixTimeout),
            ErrorCategory.LocalIo when error.IsDiskFull => new(RetryAction.BlockDiskFull),
            _ => new(RetryAction.Fail),
        };
    }

    private RetryDecision WaitOrFail(int attempt, int maxRetries)
    {
        if (attempt >= maxRetries)
        {
            return new(RetryAction.Fail);
        }

        var factor = 1 + (Jitter * ((2 * _random.NextDouble()) - 1));
        return new(RetryAction.WaitForRetry, timeProvider.GetUtcNow() + (Backoff[attempt] * factor));
    }
}
