using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Tests.Persistence;
using YoutubeDownloadHelper.Core.Tests.Scheduling;
using static YoutubeDownloadHelper.Core.Tests.Auth.Cookies;

namespace YoutubeDownloadHelper.Core.Tests.Auth;

/// <summary>
/// The verification, the keep-alive ping and the recovery ladder (PLAN.md §5.7), on a fake clock. Verifications run
/// FakeYtDlp, which is logged in with a non-empty <c>LOGIN_INFO</c> and no <c>*PSIDTS</c> ending in <c>-dead</c>; a test
/// scripts a single outcome only to simulate a glitch.
/// </summary>
public sealed class AuthVerifierTests
{
    private static readonly TimeSpan KeepAlive = new AuthOptions().KeepAliveInterval;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_verified_login_is_marked_known_good_and_requeues_the_items_waiting_on_auth()
    {
        await using var h = await SchedulerHarness.StartAsync(verifier: true);
        var waiting = await h.AddAsync("https://fake.test/members-only?id=verify00001");
        await h.WaitForAsync(waiting, ItemState.WaitingAuth);

        var login = await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);

        await h.WaitForAsync(waiting, ItemState.Completed);
        Assert.Equal(AuthStatus.Valid, h.Auth.Status.Status);
        Assert.True(h.CookieStore.Find(login.Number)!.IsKnownGood);
        Assert.Contains(await h.ItemEventsAsync(waiting), e => e.Reason == ItemEventReasons.AuthRestored);
        Assert.Contains(h.DrainEvents(), e => e is AuthChangedEvent { Auth.Status: AuthStatus.Valid, PreviousStatus: AuthStatus.Unknown });
    }

    [Fact]
    public async Task Cookies_that_fail_without_a_rollback_target_or_a_client_expire_after_one_retry()
    {
        await using var h = await SchedulerHarness.StartAsync(verifier: true);
        var waiting = await h.AddAsync("https://fake.test/members-only?id=verify00002");
        await h.WaitForAsync(waiting, ItemState.WaitingAuth);

        await h.Auth.SetCookiesAsync(Login("psidts-0-dead"), CookieSource.Import, Ct);

        await WaitForStatusAsync(h, AuthStatus.Expired);
        Assert.Equal(2, h.Probe.Calls);
        var events = h.DrainEvents();
        Assert.Contains(events, e => e is AuthChangedEvent { Auth.Status: AuthStatus.Expired, PreviousStatus: AuthStatus.Unknown });
        Assert.DoesNotContain(events, e => e is AuthRefreshRequestedEvent);
        Assert.Equal([CookieSource.Import], h.CookieStore.Generations.Select(g => g.Source));
        Assert.Null(await h.Auth.CheckOutAsync(Ct));
        Assert.Equal(ItemState.WaitingAuth, (await h.ItemAsync(waiting)).State);

        // Expired cookies are neither kept alive nor verified after job failures, until someone asks.
        h.AuthVerification.RequestVerification();
        await h.AdvanceVerifierAsync(1, KeepAlive);
        Assert.Equal(1, (await h.WaitVerifierIdleAsync(1)).Runs);
        Assert.Equal(2, h.Probe.Calls);
    }

    [Fact]
    public async Task Step_a_a_glitch_is_recovered_by_the_retry_which_requeues_the_waiting_items()
    {
        await using var h = await SchedulerHarness.StartAsync(verifier: true);
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        await WaitForStatusAsync(h, AuthStatus.Valid);

        // A bot check fails even when logged in; the item waits for auth.
        var waiting = await h.AddAsync("https://fake.test/bot-check?id=verify00003");
        await h.WaitForAsync(waiting, ItemState.WaitingAuth);

        h.Probe.Next(AuthProbeOutcome.Invalid);
        var verification = await h.Verifier.VerifyAsync(Ct);

        Assert.Equal(new AuthVerification(new AuthInfo(AuthStatus.Valid), Recovering: false), verification);
        Assert.Equal(3, h.Probe.Calls);
        Assert.DoesNotContain(h.CookieStore.Generations, g => g.Source == CookieSource.Rollback);
        await SchedulerHarness.WaitUntilAsync(
            async () => (await h.ItemEventsAsync(waiting)).Any(e => e.Reason == ItemEventReasons.AuthRestored),
            () => "the waiting item to be requeued");
    }

    [Fact]
    public async Task Step_b_rolls_back_to_the_generation_the_browser_refresh_started_from()
    {
        await using var h = await SchedulerHarness.StartAsync(verifier: true);
        var login = await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        await WaitForStatusAsync(h, AuthStatus.Valid);

        // A refresh whose jar passed when it completed, but whose session Google later stopped accepting.
        var begun = await h.Lease.BeginAsync(Ct);
        h.Probe.Next(AuthProbeOutcome.Valid);
        var refreshed = await h.Lease.CompleteAsync(begun.LeaseId!, Login("psidts-1-dead"), Ct);
        Assert.Equal(RefreshCompleteStatus.Accepted, refreshed.Status);
        Assert.Equal(login.Number, h.CookieStore.RollbackTarget!.Number);

        // A job with the dead jar fails with an auth error and asks for a verification, which waits for the throttle.
        var waiting = await h.AddAsync("https://fake.test/members-only?id=verify00004");
        await h.WaitForAsync(waiting, ItemState.WaitingAuth);
        await h.AdvanceVerifierAsync(1, new AuthOptions().ErrorVerificationInterval);

        await h.WaitForAsync(waiting, ItemState.Completed);
        var rollback = Assert.Single(h.CookieStore.Generations, g => g.Source == CookieSource.Rollback);
        Assert.Equal(login.Number, rollback.BasedOn);
        Assert.True(rollback.IsKnownGood);
        Assert.Equal(AuthStatus.Valid, h.Auth.Status.Status);
        // The login, the refresh's own check, then the ladder: the dead jar twice and the rollback target.
        Assert.Equal(
            ["psidts-0", "psidts-1-dead", "psidts-1-dead", "psidts-1-dead", "psidts-0"],
            h.Probe.Probed.Select(j => j.Value("__Secure-1PSIDTS")));
        Assert.DoesNotContain(h.DrainEvents(), e => e is AuthChangedEvent { Auth.Status: AuthStatus.Expired } or AuthRefreshRequestedEvent);
    }

    [Fact]
    public async Task Step_c_a_browser_refresh_the_engine_asks_for_recovers_the_login()
    {
        await using var h = await SchedulerHarness.StartAsync(verifier: true);
        h.Presence.Connected = true;
        var waiting = await h.AddAsync("https://fake.test/members-only?id=verify00005");
        await h.WaitForAsync(waiting, ItemState.WaitingAuth);

        await h.Auth.SetCookiesAsync(Login("psidts-0-dead"), CookieSource.Login, Ct);
        await WaitForEventAsync<AuthRefreshRequestedEvent>(h);

        var begun = await h.Lease.BeginAsync(Ct);
        Assert.Equal("psidts-0-dead", begun.Jar!.Value("__Secure-1PSIDTS"));
        var refreshed = await h.Lease.CompleteAsync(begun.LeaseId!, Login("psidts-1"), Ct);

        Assert.Equal(RefreshCompleteStatus.Accepted, refreshed.Status);
        await h.WaitForAsync(waiting, ItemState.Completed);
        Assert.Equal(AuthStatus.Valid, h.Auth.Status.Status);
        Assert.Equal(CookieSource.BrowserRefresh, h.CookieStore.Find(refreshed.Stored!.Number)!.Source);
        Assert.DoesNotContain(h.DrainEvents(), e => e is AuthChangedEvent { Auth.Status: AuthStatus.Expired });
    }

    [Fact]
    public async Task Step_d_without_a_refresh_in_time_the_login_expires_and_verify_answers_while_it_waits()
    {
        await using var h = await SchedulerHarness.StartAsync(verifier: true);
        h.Presence.Connected = true;
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        await WaitForStatusAsync(h, AuthStatus.Valid);

        h.Probe.Next(AuthProbeOutcome.Invalid, AuthProbeOutcome.Invalid);
        var verification = await h.Verifier.VerifyAsync(Ct);

        Assert.Equal(new AuthVerification(new AuthInfo(AuthStatus.Valid), Recovering: true), verification);
        Assert.Contains(h.DrainEvents(), e => e is AuthRefreshRequestedEvent);
        Assert.Equal(AuthStatus.Valid, h.Auth.Status.Status);

        h.Time.Advance(new AuthOptions().RefreshStartTimeout);
        await WaitForStatusAsync(h, AuthStatus.Expired);
        Assert.Contains(h.DrainEvents(), e => e is AuthChangedEvent { Auth.Status: AuthStatus.Expired, PreviousStatus: AuthStatus.Valid });
    }

    [Fact]
    public async Task Step_d_a_refresh_lease_that_expires_ends_the_wait()
    {
        await using var h = await SchedulerHarness.StartAsync(verifier: true);
        h.Presence.Connected = true;
        await h.Auth.SetCookiesAsync(Login("psidts-0-dead"), CookieSource.Login, Ct);
        await WaitForEventAsync<AuthRefreshRequestedEvent>(h);

        var begun = await h.Lease.BeginAsync(Ct);
        h.Time.Advance(new AuthOptions().RefreshStartTimeout);
        Assert.Equal(AuthStatus.Unknown, h.Auth.Status.Status);

        h.Time.Advance(new AuthOptions().LeaseDuration);
        await WaitForStatusAsync(h, AuthStatus.Expired);
        Assert.False(h.Lease.IsActive);
        Assert.Equal(RefreshCompleteStatus.NoLease, (await h.Lease.CompleteAsync(begun.LeaseId!, Login(), Ct)).Status);
    }

    [Fact]
    public async Task The_keep_alive_ping_runs_every_six_hours_even_when_idle_and_merges_its_writeback()
    {
        var options = new AuthOptions { VerificationUrl = "https://fake.test/subscriptions?rotate=SIDCC" };
        await using var h = await SchedulerHarness.StartAsync(verifier: true, authOptions: options);
        var start = h.Time.GetUtcNow();
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);

        var idle = await h.WaitVerifierIdleAsync(1);
        Assert.Equal(start + KeepAlive, idle.WakeAt);
        Assert.Equal((CookieSource.Ping, "sidcc-0-rotated"), (h.CookieStore.Current!.Source, h.CookieStore.Current.Jar.Value("SIDCC")));

        h.Time.Advance(KeepAlive - TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await h.WaitVerifierIdleAsync(1)).Runs);

        h.Time.Advance(TimeSpan.FromMinutes(1));
        idle = await h.WaitVerifierIdleAsync(2);
        Assert.Equal(start + (2 * KeepAlive), idle.WakeAt);
        Assert.Equal("sidcc-0-rotated-rotated", h.CookieStore.Current!.Jar.Value("SIDCC"));
        Assert.Equal(AuthStatus.Valid, h.Auth.Status.Status);
    }

    [Fact]
    public async Task An_inconclusive_verification_changes_nothing_and_runs_again_soon()
    {
        await using var h = await SchedulerHarness.StartAsync(verifier: true);
        h.ToolHealth.Ready = false;
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);

        var idle = await h.WaitVerifierIdleAsync(1);
        Assert.Equal(AuthStatus.Unknown, h.Auth.Status.Status);
        Assert.Equal(h.Time.GetUtcNow() + new AuthOptions().InconclusiveRetryDelay, idle.WakeAt);

        h.ToolHealth.Ready = true;
        h.Time.Advance(new AuthOptions().InconclusiveRetryDelay);
        await WaitForStatusAsync(h, AuthStatus.Valid);
    }

    [Fact]
    public async Task Job_auth_failures_ask_for_at_most_one_verification_per_interval_and_valid_cookies_requeue_nothing()
    {
        await using var h = await SchedulerHarness.StartAsync(verifier: true);
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        await WaitForStatusAsync(h, AuthStatus.Valid);

        var first = await h.AddAsync("https://fake.test/bot-check?id=verify00006");
        var second = await h.AddAsync("https://fake.test/bot-check?id=verify00007");
        await h.WaitForAsync(first, ItemState.WaitingAuth);
        await h.WaitForAsync(second, ItemState.WaitingAuth);
        Assert.Equal(1, (await h.WaitVerifierIdleAsync(1, h.Time.GetUtcNow() + new AuthOptions().ErrorVerificationInterval)).Runs);

        h.Time.Advance(new AuthOptions().ErrorVerificationInterval);
        Assert.Equal(2, (await h.WaitVerifierIdleAsync(2)).Runs);

        // The cookies are fine, so the items' failures were not about them: they keep waiting.
        Assert.Equal(AuthStatus.Valid, h.Auth.Status.Status);
        Assert.Equal((ItemState.WaitingAuth, ItemState.WaitingAuth), ((await h.ItemAsync(first)).State, (await h.ItemAsync(second)).State));
        Assert.DoesNotContain(await h.ItemEventsAsync(first), e => e.Reason == ItemEventReasons.AuthRestored);
    }

    [Fact]
    public async Task The_engine_start_verifies_stored_cookies_and_a_log_out_needs_no_verification()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        await using (var first = await SchedulerHarness.StartAsync(root))
        {
            await first.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        }

        await using var h = await SchedulerHarness.StartAsync(root, verifier: true);
        await WaitForStatusAsync(h, AuthStatus.Valid);

        // Logged out, nothing is kept alive: when the ping is due, the verifier finds no cookies and waits for a request.
        await h.Auth.LogOutAsync(Ct);
        await h.AdvanceVerifierAsync(1, KeepAlive);
        await SchedulerHarness.WaitUntilAsync(
            () => Task.FromResult(h.Verifier.Idle is { WakeAt: null }), () => "the verifier to wait without a timer");
        Assert.Equal(1, h.Probe.Calls);
        Assert.Equal(new AuthVerification(new AuthInfo(AuthStatus.NotLoggedIn), false), await h.Verifier.VerifyAsync(Ct));
    }

    private static Task WaitForStatusAsync(SchedulerHarness h, AuthStatus status) => SchedulerHarness.WaitUntilAsync(
        () => Task.FromResult(h.Auth.Status.Status == status),
        () => $"the auth status to be {status}, but it is {h.Auth.Status.Status}");

    private static async Task WaitForEventAsync<T>(SchedulerHarness h)
        where T : EngineEvent
    {
        var seen = false;
        await SchedulerHarness.WaitUntilAsync(
            () =>
            {
                seen |= h.DrainEvents().OfType<T>().Any();
                return Task.FromResult(seen);
            },
            () => $"a {typeof(T).Name}");
    }
}
