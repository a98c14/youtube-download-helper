using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Core.Events;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>The recovery ladder step that brought the cookies back (PLAN.md §5.7 point 5), for the telemetry.</summary>
public enum AuthRecoveryStep
{
    /// <summary>The first verification passed.</summary>
    None,

    /// <summary>(a) The second try passed: the first failure was a glitch.</summary>
    Retry,

    /// <summary>(b) The generation the latest browser refresh started from passed.</summary>
    Rollback,

    /// <summary>(c) A browser refresh the engine asked for produced cookies that passed.</summary>
    BrowserRefresh,
}

/// <summary>The outcome of a verification for a caller of <see cref="AuthVerifier.VerifyAsync"/>.</summary>
/// <param name="Auth">The login status.</param>
/// <param name="Recovering">The cookies failed, and the engine waits for a browser refresh before it gives up on them.</param>
public sealed record AuthVerification(AuthInfo Auth, bool Recovering);

/// <summary>
/// Verifies the cookies (PLAN.md §5.7 "Verification", points 3 and 5): a flat fetch of the subscriptions feed
/// (<see cref="IAuthProbe"/>), whose writeback is merged like a job's (<see cref="CookieSource.Ping"/>). It runs when the
/// engine starts with cookies stored, after a login or import, when <c>POST /auth/verify</c> asks, after an Auth-category
/// job failure (at most once per <see cref="AuthOptions.ErrorVerificationInterval"/>), and as the keep-alive ping every
/// <see cref="AuthOptions.KeepAliveInterval"/>, even when idle. An inconclusive run (offline, tools missing) changes
/// nothing and runs again after <see cref="AuthOptions.InconclusiveRetryDelay"/>.
/// <para>
/// When the cookies fail, the recovery ladder runs before the user is bothered: (a) verify once more → (b) roll back to
/// the generation the latest browser refresh started from and verify it → (c) if a client is connected, ask it for a
/// browser refresh (<see cref="AuthRefreshRequestedEvent"/>) and wait for one to complete
/// (<see cref="BrowserRefreshLease"/>) → (d) only then <see cref="AuthStatus.Expired"/>, which the API turns into the
/// "log in again" notification. Valid cookies that were not valid before, or that the ladder brought back, requeue every
/// item waiting on auth. A login, import or log-out while a verification runs voids its result.
/// </para>
/// </summary>
public sealed partial class AuthVerifier(
    AuthManager auth,
    IAuthProbe probe,
    BrowserRefreshLease lease,
    IUiClientPresence presence,
    AuthVerificationRequests requests,
    EventBus bus,
    AuthOptions options,
    TimeProvider time,
    ILogger<AuthVerifier> logger) : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly List<TaskCompletionSource<AuthVerification>> _waiters = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private bool _started;
    private bool _stopped;

    // Loop only.
    private long _verifiedLineage = -1;
    private DateTimeOffset? _lastRun;
    private DateTimeOffset? _retryAt;
    private bool _errorRequested;

    private VerifierIdle? _idle;
    private long _runs;

    /// <summary>For tests and diagnostics: while the loop waits with its timer armed, when it wakes (null: only on a request).</summary>
    public VerifierIdle? Idle => Volatile.Read(ref _idle);

    /// <summary>How many verification passes have run, for tests and diagnostics.</summary>
    public long Runs => Interlocked.Read(ref _runs);

    /// <summary>Starts the loop. Call once, after the queue actor has started.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            throw new InvalidOperationException("The verifier is already started.");
        }

        _started = true;
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started || _stopped)
        {
            return;
        }

        _stopped = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<TaskCompletionSource<AuthVerification>> waiters;
        lock (_lock)
        {
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetCanceled(CancellationToken.None);
        }
    }

    /// <summary>
    /// Verifies the cookies now (<c>POST /auth/verify</c>), not throttled. Completes with the status when the verification
    /// is done, or as soon as the ladder waits for a browser refresh (<see cref="AuthVerification.Recovering"/>).
    /// </summary>
    public Task<AuthVerification> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var waiter = new TaskCompletionSource<AuthVerification>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_stopped)
            {
                throw new InvalidOperationException("The verifier is stopped.");
            }

            _waiters.Add(waiter);
        }

        requests.Wake();
        return waiter.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    // ---- The loop -----------------------------------------------------------------------------------------------

    private async Task LoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var (run, waiters, next) = Decide(time.GetUtcNow());
                if (run)
                {
                    Volatile.Write(ref _idle, null);
                    await RunAsync(waiters, stopping).ConfigureAwait(false);
                    Interlocked.Increment(ref _runs);
                    continue;
                }

                await WaitAsync(next, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRunFailed(logger, ex);
                _retryAt = time.GetUtcNow() + options.InconclusiveRetryDelay;
            }
        }
    }

    /// <summary>Whether to verify now, the callers it answers, and otherwise when to look again (null: on a request only).</summary>
    private (bool Run, List<TaskCompletionSource<AuthVerification>> Waiters, DateTimeOffset? Next) Decide(DateTimeOffset now)
    {
        if (requests.TakePending())
        {
            _errorRequested = true;
        }

        List<TaskCompletionSource<AuthVerification>> waiters;
        lock (_lock)
        {
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        if (waiters.Count > 0)
        {
            return (true, waiters, null);
        }

        var status = auth.Status.Status;
        if (status == AuthStatus.NotLoggedIn)
        {
            _errorRequested = false;
            _retryAt = null;
            return (false, waiters, null);
        }

        // Started with cookies stored, or a login or import since the last verification.
        if (auth.Lineage != _verifiedLineage)
        {
            return (true, waiters, null);
        }

        if (status == AuthStatus.Expired)
        {
            // Nothing to keep alive or recover; a login starts over, POST /auth/verify still verifies.
            _errorRequested = false;
            _retryAt = null;
            return (false, waiters, null);
        }

        DateTimeOffset? next = null;
        void Due(DateTimeOffset at) => next = next is { } n && n <= at ? n : at;

        if (_errorRequested)
        {
            Due(_lastRun is { } last ? last + options.ErrorVerificationInterval : now);
        }

        if (_retryAt is { } retry)
        {
            Due(retry);
        }

        Due((_lastRun ?? now) + options.KeepAliveInterval);
        return (next <= now, waiters, next);
    }

    private async Task WaitAsync(DateTimeOffset? until, CancellationToken stopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var woken = requests.WaitAsync(cts.Token);
        Task timer = until is { } at ? Task.Delay(Max(at - time.GetUtcNow(), TimeSpan.Zero), time, cts.Token) : Task.Delay(Timeout.Infinite, cts.Token);
        Volatile.Write(ref _idle, new VerifierIdle(until, Runs));
        try
        {
            await Task.WhenAny(woken, timer).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _idle, null);
            await cts.CancelAsync().ConfigureAwait(false);
        }

        stopping.ThrowIfCancellationRequested();

        static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }

    // ---- One verification and the ladder ------------------------------------------------------------------------

    private async Task RunAsync(List<TaskCompletionSource<AuthVerification>> waiters, CancellationToken stopping)
    {
        _lastRun = time.GetUtcNow();
        _errorRequested = false;
        _retryAt = null;

        var checkout = await auth.CheckOutCurrentAsync(stopping).ConfigureAwait(false);
        _verifiedLineage = checkout?.Lineage ?? auth.Lineage;
        try
        {
            if (checkout is null)
            {
                return;
            }

            await VerifyCheckoutAsync(checkout, waiters, stopping).ConfigureAwait(false);
        }
        finally
        {
            Settle(waiters, recovering: false);
        }
    }

    private async Task VerifyCheckoutAsync(CookieCheckout checkout, List<TaskCompletionSource<AuthVerification>> waiters, CancellationToken stopping)
    {
        var first = await ProbeAsync(checkout, stopping).ConfigureAwait(false);
        if (first.Outcome == AuthProbeOutcome.Valid)
        {
            await AcceptAsync(checkout, first, AuthRecoveryStep.None).ConfigureAwait(false);
            return;
        }

        if (first.Outcome == AuthProbeOutcome.Inconclusive)
        {
            Inconclusive(checkout, first);
            return;
        }

        LogFailed(logger, checkout.Generation.Number, checkout.Generation.Source, first.Error?.Code);

        // (a) Once more, in case it was a glitch.
        var again = await ProbeAsync(checkout, stopping).ConfigureAwait(false);
        if (again.Outcome == AuthProbeOutcome.Valid)
        {
            await AcceptAsync(checkout, again, AuthRecoveryStep.Retry).ConfigureAwait(false);
            return;
        }

        if (again.Outcome == AuthProbeOutcome.Inconclusive)
        {
            Inconclusive(checkout, again);
            return;
        }

        // (b) The generation the latest browser refresh started from; anything older holds a dead *PSIDTS.
        var current = checkout;
        if (await auth.RollBackAsync(checkout, stopping).ConfigureAwait(false) is { } rolledBack)
        {
            current = rolledBack;
            var restored = await ProbeAsync(rolledBack, stopping).ConfigureAwait(false);
            if (restored.Outcome == AuthProbeOutcome.Valid)
            {
                await AcceptAsync(rolledBack, restored, AuthRecoveryStep.Rollback).ConfigureAwait(false);
                return;
            }

            if (restored.Outcome == AuthProbeOutcome.Inconclusive)
            {
                Inconclusive(rolledBack, restored);
                return;
            }
        }

        if (auth.Lineage != current.Lineage)
        {
            return;
        }

        // (c) A browser refresh, if a client is there to run it.
        if (presence.AnyClientConnected)
        {
            var refreshed = lease.WaitForRefreshAsync(options.RefreshStartTimeout, stopping);
            bus.Publish(new AuthRefreshRequestedEvent());
            LogRefreshRequested(logger, current.Generation.Number);
            Settle(waiters, recovering: true);
            if (await refreshed.ConfigureAwait(false))
            {
                // The lease verified and stored the new jar; the items failed while it was bad, so release them here.
                LogRecovered(logger, AuthRecoveryStep.BrowserRefresh, SessionAge(current.Generation));
                await auth.ReleaseWaitingAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }

        // (d) The user has to log in again.
        if (await auth.SetVerifiedAsync(current, AuthStatus.Expired, cancellationToken: CancellationToken.None).ConfigureAwait(false) is not null)
        {
            LogExpired(logger, current.Generation.Number, current.Generation.SessionStartedAt, SessionAge(current.Generation));
        }
    }

    /// <summary>One probe, with its writeback merged as a ping (nothing after an Auth failure).</summary>
    private async Task<AuthProbeResult> ProbeAsync(CookieCheckout checkout, CancellationToken stopping)
    {
        var result = await probe.ProbeAsync(checkout.Generation.Jar, stopping).ConfigureAwait(false);
        LogProbed(logger, checkout.Generation.Number, result.Outcome);
        return result;
    }

    private async Task AcceptAsync(CookieCheckout checkout, AuthProbeResult result, AuthRecoveryStep step)
    {
        // No cancellation from here: a verified result is recorded even while the engine stops.
        var before = await auth.SetVerifiedAsync(checkout, AuthStatus.Valid, result.Account, CancellationToken.None).ConfigureAwait(false);
        if (before is not { } previous)
        {
            // Logged out or in again meanwhile: the next pass verifies the new cookies.
            return;
        }

        if (result.WrittenBack is { } written)
        {
            await auth.MergeWritebackAsync(checkout, written, failedWithAuth: false, CookieSource.Ping, CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (step != AuthRecoveryStep.None)
        {
            LogRecovered(logger, step, SessionAge(checkout.Generation));
        }
        else
        {
            LogValid(logger, checkout.Generation.Number, checkout.Generation.Source, SessionAge(checkout.Generation));
        }

        if (previous != AuthStatus.Valid || step != AuthRecoveryStep.None)
        {
            var released = await auth.ReleaseWaitingAsync(CancellationToken.None).ConfigureAwait(false);
            if (released > 0)
            {
                LogReleased(logger, released);
            }
        }
    }

    private void Inconclusive(CookieCheckout checkout, AuthProbeResult result)
    {
        _retryAt = time.GetUtcNow() + options.InconclusiveRetryDelay;
        LogInconclusive(logger, checkout.Generation.Number, result.Error?.Code, options.InconclusiveRetryDelay);
    }

    private void Settle(List<TaskCompletionSource<AuthVerification>> waiters, bool recovering)
    {
        if (waiters.Count == 0)
        {
            return;
        }

        var verification = new AuthVerification(auth.Status, recovering);
        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(verification);
        }
    }

    private TimeSpan? SessionAge(CookieGeneration generation) =>
        generation.SessionStartedAt is { } started ? time.GetUtcNow() - started : null;

    [LoggerMessage(Level = LogLevel.Error, Message = "An auth verification failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Verified cookie generation {Generation}: {Outcome}")]
    private static partial void LogProbed(ILogger logger, long generation, AuthProbeOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cookie generation {Generation} ({Source}) is valid; session age {SessionAge}")]
    private static partial void LogValid(ILogger logger, long generation, CookieSource source, TimeSpan? sessionAge);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cookie generation {Generation} ({Source}) failed verification ({Code}); starting the recovery ladder")]
    private static partial void LogFailed(ILogger logger, long generation, CookieSource source, string? code);

    [LoggerMessage(Level = LogLevel.Information, Message = "The login was recovered by {Step}; session age {SessionAge}")]
    private static partial void LogRecovered(ILogger logger, AuthRecoveryStep step, TimeSpan? sessionAge);

    [LoggerMessage(Level = LogLevel.Information, Message = "Asked a connected client for a browser refresh of cookie generation {Generation}")]
    private static partial void LogRefreshRequested(ILogger logger, long generation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The login expired (generation {Generation}); the session started at {SessionStartedAt} and lasted {SessionLifetime}")]
    private static partial void LogExpired(ILogger logger, long generation, DateTimeOffset? sessionStartedAt, TimeSpan? sessionLifetime);

    [LoggerMessage(Level = LogLevel.Information, Message = "Verification of cookie generation {Generation} was inconclusive ({Code}); trying again in {Delay}")]
    private static partial void LogInconclusive(ILogger logger, long generation, string? code, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Information, Message = "Requeued {Count} items that were waiting on auth")]
    private static partial void LogReleased(ILogger logger, int count);
}

/// <summary>What the verifier's loop waits for (<see cref="AuthVerifier.Idle"/>).</summary>
/// <param name="WakeAt">When its timer fires; null when only a request wakes it.</param>
/// <param name="Runs">How many passes had run (<see cref="AuthVerifier.Runs"/>).</param>
public sealed record VerifierIdle(DateTimeOffset? WakeAt, long Runs);
