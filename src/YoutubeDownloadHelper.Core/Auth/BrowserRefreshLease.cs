using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Core.Events;

namespace YoutubeDownloadHelper.Core.Auth;

public enum RefreshBeginStatus
{
    Begun,

    /// <summary>No cookies are stored.</summary>
    NotLoggedIn,

    /// <summary>Another refresh holds the lease.</summary>
    InProgress,
}

/// <param name="Status">Whether the lease was taken.</param>
/// <param name="LeaseId">The lease, to complete it with.</param>
/// <param name="ExpiresAt">When the lease ends on its own.</param>
/// <param name="Jar">The current cookies, to load into the browser.</param>
public sealed record RefreshBeginResult(RefreshBeginStatus Status, string? LeaseId = null, DateTimeOffset? ExpiresAt = null, CookieJar? Jar = null);

public enum RefreshCompleteStatus
{
    /// <summary>Verified and stored as the new current generation.</summary>
    Accepted,

    /// <summary>The lease is unknown, already completing, completed or expired.</summary>
    NoLease,

    /// <summary>The user logged in, imported cookies or logged out since the refresh began.</summary>
    Superseded,

    /// <summary>The new cookies failed verification.</summary>
    Rejected,

    /// <summary>The new cookies could not be verified (offline, tools missing).</summary>
    Unverified,
}

/// <param name="Status">What happened to the cookies.</param>
/// <param name="Stored">The new generation, when accepted.</param>
public sealed record RefreshCompleteResult(RefreshCompleteStatus Status, CookieGeneration? Stored = null);

/// <summary>
/// The engine side of the browser refresh (PLAN.md §5.7 point 4). <see cref="BeginAsync"/> hands out the current jar
/// and takes a lease that holds new cookie-using job starts (<see cref="IJobStartHold"/>) for at most
/// <see cref="AuthOptions.LeaseDuration"/>; running jobs continue with their copies. <see cref="CompleteAsync"/> verifies
/// the refreshed jar and stores it (<see cref="CookieSource.BrowserRefresh"/>, known-good) only if it passes; either way
/// it ends the lease. A lease that is not completed in time expires: the stored jar stays, and the next refresh can start
/// from it. One lease at a time. The client side (a hidden WebView2) is the app's.
/// <para>
/// Step (c) of the recovery ladder waits on <see cref="WaitForRefreshAsync"/> for a lease to end.
/// </para>
/// </summary>
public sealed partial class BrowserRefreshLease(
    AuthManager auth,
    IAuthProbe probe,
    EventBus bus,
    AuthOptions options,
    TimeProvider time,
    ILogger<BrowserRefreshLease> logger) : IJobStartHold, IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<TaskCompletionSource<bool>> _waiters = [];
    private Lease? _active;

    /// <summary>
    /// While a lease is held: when it expires. Completing a lease ends the hold early. Jobs that would run without
    /// cookies (the login expired) are not held.
    /// </summary>
    public DateTimeOffset? HeldUntil
    {
        get
        {
            DateTimeOffset expiresAt;
            lock (_lock)
            {
                if (_active is not { } lease || time.GetUtcNow() >= lease.ExpiresAt)
                {
                    return null;
                }

                expiresAt = lease.ExpiresAt;
            }

            return auth.Status.Status is AuthStatus.NotLoggedIn or AuthStatus.Expired ? null : expiresAt;
        }
    }

    /// <summary>Whether a lease is held (begun and neither completed nor expired).</summary>
    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _active is not null;
            }
        }
    }

    /// <summary>Takes the lease and returns the current jar, unless nothing is stored or another refresh holds it.</summary>
    public async Task<RefreshBeginResult> BeginAsync(CancellationToken cancellationToken = default)
    {
        var checkout = await auth.CheckOutCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (checkout is null)
        {
            return new RefreshBeginResult(RefreshBeginStatus.NotLoggedIn);
        }

        Lease lease;
        lock (_lock)
        {
            if (_active is not null)
            {
                return new RefreshBeginResult(RefreshBeginStatus.InProgress);
            }

            lease = new Lease(
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                checkout,
                time.GetUtcNow() + options.LeaseDuration);
            _active = lease;
            lease.Timer = time.CreateTimer(_ => Expire(lease), null, options.LeaseDuration, Timeout.InfiniteTimeSpan);
        }

        bus.Publish(new AuthRefreshLeaseChangedEvent(true));
        LogBegun(logger, checkout.Generation.Number, options.LeaseDuration);
        return new RefreshBeginResult(RefreshBeginStatus.Begun, lease.Id, lease.ExpiresAt, checkout.Generation.Jar);
    }

    /// <summary>
    /// Verifies <paramref name="jar"/> and keeps it only if it passes, then ends the lease. Requeues the items waiting on
    /// auth when it was not valid before.
    /// </summary>
    public async Task<RefreshCompleteResult> CompleteAsync(string leaseId, CookieJar jar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leaseId);
        ArgumentNullException.ThrowIfNull(jar);
        jar = jar.YouTubeOnly();
        if (jar.Count == 0)
        {
            throw new ArgumentException("The jar has no youtube.com cookies.", nameof(jar));
        }

        Lease lease;
        lock (_lock)
        {
            if (_active is not { Completing: false } active || !string.Equals(active.Id, leaseId, StringComparison.Ordinal))
            {
                return new RefreshCompleteResult(RefreshCompleteStatus.NoLease);
            }

            lease = active;
            lease.Completing = true;
        }

        var result = new RefreshCompleteResult(RefreshCompleteStatus.Unverified);
        try
        {
            // Not cancelled by the request: a refreshed jar that passed must not be lost because the client gave up.
            result = await VerifyAndStoreAsync(lease, jar).ConfigureAwait(false);
            return result;
        }
        finally
        {
            End(lease, result.Status == RefreshCompleteStatus.Accepted);
            LogCompleted(logger, lease.Begun.Generation.Number, result.Status, result.Stored?.Number);
        }
    }

    /// <summary>
    /// Completes with whether the next lease to end stored a refreshed jar: true when one is accepted, false when it is
    /// rejected or expires, or when no lease begins within <paramref name="startTimeout"/>. Registers before it returns,
    /// so a refresh that begins right after the call counts.
    /// </summary>
    public Task<bool> WaitForRefreshAsync(TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _waiters.Add(waiter);
        }

        return WaitAsync(waiter, startTimeout, cancellationToken);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _active?.Timer?.Dispose();
            _active = null;
            foreach (var waiter in _waiters)
            {
                waiter.TrySetResult(false);
            }

            _waiters.Clear();
        }
    }

    private async Task<bool> WaitAsync(TaskCompletionSource<bool> waiter, TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        try
        {
            using (var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var timeout = Task.Delay(startTimeout, time, delay.Token);
                if (await Task.WhenAny(waiter.Task, timeout).ConfigureAwait(false) == timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (_lock)
                    {
                        // Nobody began a refresh in time. One that is under way ends on its own (completed or expired).
                        if (_active is null && !waiter.Task.IsCompleted)
                        {
                            _waiters.Remove(waiter);
                            return false;
                        }
                    }
                }

                await delay.CancelAsync().ConfigureAwait(false);
            }

            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _waiters.Remove(waiter);
            }

            throw;
        }
    }

    private async Task<RefreshCompleteResult> VerifyAndStoreAsync(Lease lease, CookieJar jar)
    {
        var verified = await probe.ProbeAsync(jar, CancellationToken.None).ConfigureAwait(false);
        switch (verified.Outcome)
        {
            case AuthProbeOutcome.Invalid:
                return new RefreshCompleteResult(RefreshCompleteStatus.Rejected);
            case AuthProbeOutcome.Inconclusive:
                return new RefreshCompleteResult(RefreshCompleteStatus.Unverified);
        }

        if (await auth.StoreRefreshAsync(lease.Begun, jar).ConfigureAwait(false) is not { } stored)
        {
            return new RefreshCompleteResult(RefreshCompleteStatus.Superseded);
        }

        var before = await auth.SetVerifiedAsync(stored, AuthStatus.Valid, verified.Account).ConfigureAwait(false);
        if (verified.WrittenBack is { } written)
        {
            await auth.MergeWritebackAsync(stored, written, failedWithAuth: false, CookieSource.Ping).ConfigureAwait(false);
        }

        if (before is { } previous && previous != AuthStatus.Valid)
        {
            await auth.ReleaseWaitingAsync().ConfigureAwait(false);
        }

        return new RefreshCompleteResult(RefreshCompleteStatus.Accepted, stored.Generation);
    }

    private void Expire(Lease lease)
    {
        lock (_lock)
        {
            if (_active != lease)
            {
                return;
            }

            if (lease.Completing)
            {
                // The hold is over (HeldUntil), but the verification under way still decides; it ends the lease.
                bus.Publish(new AuthRefreshLeaseChangedEvent(false));
                return;
            }
        }

        End(lease, accepted: false);
        LogExpired(logger, lease.Begun.Generation.Number);
    }

    private void End(Lease lease, bool accepted)
    {
        List<TaskCompletionSource<bool>> waiters;
        lock (_lock)
        {
            if (_active != lease)
            {
                return;
            }

            _active = null;
            lease.Timer?.Dispose();
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        bus.Publish(new AuthRefreshLeaseChangedEvent(false));
        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(accepted);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Browser refresh began from cookie generation {Generation}; new cookie-using jobs wait at most {Duration}")]
    private static partial void LogBegun(ILogger logger, long generation, TimeSpan duration);

    [LoggerMessage(Level = LogLevel.Information, Message = "Browser refresh from cookie generation {Generation} completed: {Status} (stored as generation {Stored})")]
    private static partial void LogCompleted(ILogger logger, long generation, RefreshCompleteStatus status, long? stored);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Browser refresh from cookie generation {Generation} expired before it completed; the stored cookies stay")]
    private static partial void LogExpired(ILogger logger, long generation);

    private sealed class Lease(string id, CookieCheckout begun, DateTimeOffset expiresAt)
    {
        public string Id { get; } = id;

        public CookieCheckout Begun { get; } = begun;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        // Guarded by the lease's lock.
        public bool Completing { get; set; }

        public ITimer? Timer { get; set; }
    }
}
