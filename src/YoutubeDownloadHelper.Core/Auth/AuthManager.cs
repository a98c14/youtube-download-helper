using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Queue;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// A generation handed to a yt-dlp run, to merge its writeback against (<see cref="AuthManager.MergeWritebackAsync"/>).
/// </summary>
/// <param name="Generation">The generation the run was given.</param>
/// <param name="Lineage">
/// Which login it belongs to. A login, an import and a log-out start a new lineage; a writeback from an older one is
/// not merged, since its cookies belong to a session that is gone.
/// </param>
public sealed record CookieCheckout(CookieGeneration Generation, long Lineage);

/// <summary>
/// Every change to the cookie lineage (PLAN.md §5.7): a login or import, a log-out, and the writeback merges of yt-dlp
/// runs. The changes run through the <see cref="QueueActor"/> (<see cref="QueueActor.SerializeAsync{T}"/>), so they
/// are serialized with each other: a merge reads the current jar and stores the new generation in one step. Each
/// login, import and log-out publishes an <see cref="AuthChangedEvent"/>.
/// </summary>
public sealed partial class AuthManager(
    CookieJarStore store,
    QueueActor queue,
    IAuthStatusProvider status,
    IAuthVerificationTrigger verification,
    EventBus bus,
    TimeProvider time,
    ILogger<AuthManager> logger)
{
    // Only changed on the actor.
    private long _lineage;

    /// <summary>The login status (<see cref="IAuthStatusProvider"/>).</summary>
    public AuthInfo Status => status.GetStatus();

    /// <summary>
    /// Stores a jar from the login window (<see cref="CookieSource.Login"/>) or a <c>cookies.txt</c> import
    /// (<see cref="CookieSource.Import"/>) as a new lineage, asks for a verification, and requeues the items waiting on
    /// auth.
    /// </summary>
    /// <exception cref="ArgumentException">The jar has no <c>youtube.com</c> cookies, or the source is not a login or import.</exception>
    public async Task<CookieGeneration> SetCookiesAsync(CookieJar jar, CookieSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jar);
        if (source is not (CookieSource.Login or CookieSource.Import))
        {
            throw new ArgumentException("Only a login or an import replaces the cookies.", nameof(source));
        }

        if (jar.YouTubeOnly().Count == 0)
        {
            throw new ArgumentException("The jar has no youtube.com cookies.", nameof(jar));
        }

        var generation = await queue.SerializeAsync(
            () =>
            {
                var added = store.Add(jar, source);
                _lineage++;
                bus.Publish(new AuthChangedEvent(status.GetStatus()));
                return added;
            },
            cancellationToken).ConfigureAwait(false);
        LogStored(logger, generation.Number, source, generation.Jar.Count);

        verification.RequestVerification();
        await queue.ReleaseWaitingAsync(ItemTrigger.AuthRestored, cancellationToken).ConfigureAwait(false);
        return generation;
    }

    /// <summary>Deletes every stored generation (log out). Runs already given cookies keep their copies, but nothing is merged back.</summary>
    public async Task LogOutAsync(CancellationToken cancellationToken = default)
    {
        await queue.SerializeAsync(
            () =>
            {
                store.Clear();
                _lineage++;
                bus.Publish(new AuthChangedEvent(status.GetStatus()));
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        LogLoggedOut(logger);
    }

    /// <summary>
    /// The current generation for a yt-dlp run, or <see langword="null"/> when the run goes without cookies: nothing is
    /// stored, or the status is <see cref="AuthStatus.NotLoggedIn"/> or <see cref="AuthStatus.Expired"/>.
    /// </summary>
    public Task<CookieCheckout?> CheckOutAsync(CancellationToken cancellationToken = default) =>
        queue.SerializeAsync(
            () => status.GetStatus().Status is AuthStatus.NotLoggedIn or AuthStatus.Expired || store.Current is not { } current
                ? null
                : new CookieCheckout(current, _lineage),
            cancellationToken);

    /// <summary>
    /// Merges the cookies a run wrote back (<paramref name="written"/>) onto the current jar, three-way against the
    /// generation it was given (<see cref="CookieMerge"/>), and stores the result as a new generation when anything
    /// changed.
    /// </summary>
    /// <param name="given">What <see cref="CheckOutAsync"/> handed the run.</param>
    /// <param name="written">The run's cookies file as yt-dlp left it.</param>
    /// <param name="failedWithAuth">The run ended in an Auth-category failure: nothing is merged.</param>
    /// <param name="source"><see cref="CookieSource.Writeback"/> for a job or resolve, <see cref="CookieSource.Ping"/> for the keep-alive ping.</param>
    /// <param name="cancellationToken">Cancels waiting for the actor.</param>
    /// <returns>The merge, or <see langword="null"/> when the run's lineage is gone (logged out or in again since).</returns>
    public async Task<CookieMergeResult?> MergeWritebackAsync(
        CookieCheckout given,
        CookieJar written,
        bool failedWithAuth,
        CookieSource source = CookieSource.Writeback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(given);
        ArgumentNullException.ThrowIfNull(written);
        if (source is not (CookieSource.Writeback or CookieSource.Ping))
        {
            throw new ArgumentException("A writeback is a Writeback or a Ping.", nameof(source));
        }

        var (result, stored) = await queue.SerializeAsync<(CookieMergeResult?, CookieGeneration?)>(
            () =>
            {
                if (given.Lineage != _lineage || store.Current is not { } current)
                {
                    return (null, null);
                }

                var merge = CookieMerge.Merge(current.Jar, given.Generation.Jar, written, failedWithAuth, time.GetUtcNow());
                return (merge, merge.Outcome == CookieMergeOutcome.Merged ? store.Add(merge.Jar, source, current.Number) : null);
            },
            cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            LogLineageGone(logger, given.Generation.Number);
        }
        else if (stored is not null)
        {
            LogMerged(logger, given.Generation.Number, stored.BasedOn ?? 0, stored.Number, result.Updated.Count, result.Ignored.Count);
        }
        else
        {
            LogNotMerged(logger, given.Generation.Number, result.Outcome, result.Ignored.Count);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored cookie generation {Generation} ({Source}, {Count} cookies)")]
    private static partial void LogStored(ILogger logger, long generation, CookieSource source, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Logged out: the stored cookies are deleted")]
    private static partial void LogLoggedOut(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cookie writeback of a run given generation {Given} not merged: the login it belongs to is gone")]
    private static partial void LogLineageGone(ILogger logger, long given);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cookie writeback of a run given generation {Given} merged onto {Current} as generation {Generation}: {Updated} updated, {Ignored} ignored")]
    private static partial void LogMerged(ILogger logger, long given, long current, long generation, int updated, int ignored);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cookie writeback of a run given generation {Given} not merged ({Outcome}, {Ignored} ignored)")]
    private static partial void LogNotMerged(ILogger logger, long given, CookieMergeOutcome outcome, int ignored);
}
