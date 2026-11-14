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
/// Every change to the cookie lineage (PLAN.md §5.7): a login or import, a log-out, the writeback merges of yt-dlp
/// runs, a rollback and a browser refresh, and the verified login status. The changes run through the
/// <see cref="QueueActor"/> (<see cref="QueueActor.SerializeAsync{T}"/>), so they are serialized with each other: a merge
/// reads the current jar and stores the new generation in one step. Each login, import and log-out, and each change of
/// the verified status, publishes an <see cref="AuthChangedEvent"/>.
/// <para>
/// The status (<see cref="IAuthStatusProvider"/>): <see cref="AuthStatus.NotLoggedIn"/> without a jar; otherwise what the
/// last verification of this lineage found (<see cref="AuthVerifier"/>), <see cref="AuthStatus.Unknown"/> until then.
/// It lives in memory, so it is <see cref="AuthStatus.Unknown"/> after a restart until the start-up verification.
/// </para>
/// </summary>
public sealed partial class AuthManager(
    CookieJarStore store,
    QueueActor queue,
    AuthVerificationRequests verification,
    EventBus bus,
    TimeProvider time,
    ILogger<AuthManager> logger) : IAuthStatusProvider
{
    // Only changed on the actor; read anywhere.
    private long _lineage;
    private AuthInfo _verified = new(AuthStatus.Unknown);

    /// <summary>The login status.</summary>
    public AuthInfo Status => GetStatus();

    /// <summary>
    /// Which login the current jar belongs to: a login, an import and a log-out each start a new one. Starts at 0 when
    /// the engine starts.
    /// </summary>
    public long Lineage => Interlocked.Read(ref _lineage);

    public AuthInfo GetStatus() => store.Current is null ? new AuthInfo(AuthStatus.NotLoggedIn) : Volatile.Read(ref _verified);

    /// <summary>
    /// Stores a jar from the login window (<see cref="CookieSource.Login"/>) or a <c>cookies.txt</c> import
    /// (<see cref="CookieSource.Import"/>) as a new lineage and asks for a verification. The items waiting on auth are
    /// requeued once it passes (<see cref="AuthVerifier"/>), not before: a jar that is not logged in would only send
    /// them back to <c>WaitingAuth</c>.
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
                StartLineage();
                bus.Publish(new AuthChangedEvent(GetStatus()));
                return added;
            },
            cancellationToken).ConfigureAwait(false);
        LogStored(logger, generation.Number, source, generation.Jar.Count);

        verification.CookiesChanged();
        return generation;
    }

    /// <summary>Deletes every stored generation (log out). Runs already given cookies keep their copies, but nothing is merged back.</summary>
    public async Task LogOutAsync(CancellationToken cancellationToken = default)
    {
        await queue.SerializeAsync(
            () =>
            {
                store.Clear();
                StartLineage();
                bus.Publish(new AuthChangedEvent(GetStatus()));
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        verification.CookiesChanged();
        LogLoggedOut(logger);
    }

    /// <summary>
    /// The current generation for a yt-dlp run, or <see langword="null"/> when the run goes without cookies: nothing is
    /// stored, or the status is <see cref="AuthStatus.NotLoggedIn"/> or <see cref="AuthStatus.Expired"/>.
    /// </summary>
    public Task<CookieCheckout?> CheckOutAsync(CancellationToken cancellationToken = default) =>
        queue.SerializeAsync(
            () => GetStatus().Status is AuthStatus.NotLoggedIn or AuthStatus.Expired || store.Current is not { } current
                ? null
                : new CookieCheckout(current, _lineage),
            cancellationToken);

    /// <summary>The current generation whatever the status (to verify it, or to hand it to a browser refresh), or null when nothing is stored.</summary>
    public Task<CookieCheckout?> CheckOutCurrentAsync(CancellationToken cancellationToken = default) =>
        queue.SerializeAsync(() => store.Current is { } current ? new CookieCheckout(current, _lineage) : null, cancellationToken);

    /// <summary>
    /// Records what a verification of <paramref name="verified"/> found: <see cref="AuthStatus.Valid"/> also marks the
    /// generation known-good. Publishes <see cref="AuthChangedEvent"/> when the status changes.
    /// </summary>
    /// <param name="verified">The generation that was verified.</param>
    /// <param name="status"><see cref="AuthStatus.Valid"/>, or <see cref="AuthStatus.Expired"/> at the end of the recovery ladder.</param>
    /// <param name="account">The account name, if the verification showed it.</param>
    /// <param name="cancellationToken">Cancels waiting for the actor.</param>
    /// <returns>
    /// The status before, or null when the result no longer applies: the lineage is gone (logged out or in again since).
    /// </returns>
    public Task<AuthStatus?> SetVerifiedAsync(
        CookieCheckout verified, AuthStatus status, string? account = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (status is not (AuthStatus.Valid or AuthStatus.Expired))
        {
            throw new ArgumentException("A verification finds the cookies valid or expired.", nameof(status));
        }

        return queue.SerializeAsync<AuthStatus?>(
            () =>
            {
                if (verified.Lineage != _lineage || store.Current is null)
                {
                    return null;
                }

                if (status == AuthStatus.Valid)
                {
                    store.MarkKnownGood(verified.Generation.Number);
                }

                var before = GetStatus();
                var after = new AuthInfo(status, status == AuthStatus.Valid ? account ?? before.Account : before.Account);
                if (after != before)
                {
                    Volatile.Write(ref _verified, after);
                    bus.Publish(new AuthChangedEvent(after, before.Status));
                }

                return before.Status;
            },
            cancellationToken);
    }

    /// <summary>
    /// Step (b) of the recovery ladder: restores the generation the latest browser refresh started from
    /// (<see cref="CookieJarStore.RollBack"/>) as a new current generation of the same lineage.
    /// </summary>
    /// <param name="failed">The generation that failed verification.</param>
    /// <param name="cancellationToken">Cancels waiting for the actor.</param>
    /// <returns>The restored generation, or null when there is nothing to roll back to or the lineage is gone.</returns>
    public async Task<CookieCheckout?> RollBackAsync(CookieCheckout failed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failed);
        var restored = await queue.SerializeAsync(
            () => failed.Lineage == _lineage && store.RollBack() is { } generation ? new CookieCheckout(generation, _lineage) : null,
            cancellationToken).ConfigureAwait(false);
        if (restored is not null)
        {
            LogRolledBack(logger, failed.Generation.Number, restored.Generation.BasedOn ?? 0, restored.Generation.Number);
        }

        return restored;
    }

    /// <summary>
    /// Stores the jar a browser refresh produced (<see cref="CookieSource.BrowserRefresh"/>), already verified, as the new
    /// current generation, based on the one the refresh began from.
    /// </summary>
    /// <param name="begun">What the refresh was handed (<see cref="CheckOutCurrentAsync"/> at its start).</param>
    /// <param name="jar">The refreshed cookies; only the <c>youtube.com</c> ones are stored.</param>
    /// <param name="cancellationToken">Cancels waiting for the actor.</param>
    /// <returns>The new generation, or null when the lineage is gone (logged out or in again since the refresh began).</returns>
    public async Task<CookieCheckout?> StoreRefreshAsync(CookieCheckout begun, CookieJar jar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(begun);
        ArgumentNullException.ThrowIfNull(jar);
        if (jar.YouTubeOnly().Count == 0)
        {
            throw new ArgumentException("The jar has no youtube.com cookies.", nameof(jar));
        }

        var stored = await queue.SerializeAsync(
            () => begun.Lineage == _lineage && store.Current is not null
                ? new CookieCheckout(store.Add(jar, CookieSource.BrowserRefresh, begun.Generation.Number, knownGood: true), _lineage)
                : null,
            cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            LogStored(logger, stored.Generation.Number, CookieSource.BrowserRefresh, stored.Generation.Jar.Count);
        }

        return stored;
    }

    /// <summary>Requeues every item waiting on auth (valid cookies are back).</summary>
    /// <returns>How many items were requeued.</returns>
    public Task<int> ReleaseWaitingAsync(CancellationToken cancellationToken = default) =>
        queue.ReleaseWaitingAsync(ItemTrigger.AuthRestored, cancellationToken);

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

    // On the actor: a new login (or none) whose status nothing has verified yet.
    private void StartLineage()
    {
        Interlocked.Increment(ref _lineage);
        Volatile.Write(ref _verified, new AuthInfo(AuthStatus.Unknown));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rolled back from cookie generation {Failed} to {Target} as generation {Generation}")]
    private static partial void LogRolledBack(ILogger logger, long failed, long target, long generation);

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
