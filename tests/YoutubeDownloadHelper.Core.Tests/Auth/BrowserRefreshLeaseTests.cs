using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Tests.Scheduling;
using static YoutubeDownloadHelper.Core.Tests.Auth.Cookies;

namespace YoutubeDownloadHelper.Core.Tests.Auth;

/// <summary>
/// The engine side of the browser refresh (PLAN.md §5.7 point 4): the lease holds new cookie-using job starts, the
/// refreshed jar is kept only if FakeYtDlp finds it logged in, and the lease always ends (completed or expired).
/// </summary>
public sealed class BrowserRefreshLeaseTests
{
    private static readonly TimeSpan LeaseDuration = new AuthOptions().LeaseDuration;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Begin_hands_out_the_current_jar_and_holds_new_job_starts_until_the_lease_expires()
    {
        await using var h = await SchedulerHarness.StartAsync();
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);

        var begun = await h.Lease.BeginAsync(Ct);

        Assert.Equal(RefreshBeginStatus.Begun, begun.Status);
        Assert.True(Login().SequenceEqual(begun.Jar!));
        Assert.Equal(h.Time.GetUtcNow() + LeaseDuration, begun.ExpiresAt);
        Assert.Equal(begun.ExpiresAt, h.Lease.HeldUntil);

        var held = await h.AddAsync("https://fake.test/success?id=leasehold01");
        var idle = await h.WaitIdleAsync();
        Assert.Equal(ItemState.Queued, (await h.ItemAsync(held)).State);
        Assert.True(idle.WakeAt <= begun.ExpiresAt);

        await h.AdvanceAsync(LeaseDuration);
        await h.WaitForAsync(held, ItemState.Completed);
        Assert.False(h.Lease.IsActive);
        Assert.Null(h.Lease.HeldUntil);
        Assert.Equal(RefreshCompleteStatus.NoLease, (await h.Lease.CompleteAsync(begun.LeaseId!, Login("psidts-1"), Ct)).Status);
        Assert.Equal(CookieSource.Login, h.CookieStore.Current!.Source);
    }

    [Fact]
    public async Task Complete_keeps_a_valid_jar_as_a_known_good_refresh_and_releases_the_hold_at_once()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var login = await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        var waiting = await h.AddAsync("https://fake.test/bot-check?id=leasewait01");
        await h.WaitForAsync(waiting, ItemState.WaitingAuth);
        var begun = await h.Lease.BeginAsync(Ct);
        var held = await h.AddAsync("https://fake.test/success?id=leasehold02");
        await h.WaitIdleAsync();

        var completed = await h.Lease.CompleteAsync(begun.LeaseId!, Login("psidts-1").With(Yt("SID", "x", domain: ".google.com")), Ct);

        Assert.Equal(RefreshCompleteStatus.Accepted, completed.Status);
        var current = h.CookieStore.Find(completed.Stored!.Number)!;
        Assert.Equal((CookieSource.BrowserRefresh, (long?)login.Number, (long?)login.Number), (current.Source, current.BasedOn, current.RollbackTo));
        Assert.True(current.IsKnownGood);
        Assert.True(Login("psidts-1").SequenceEqual(current.Jar), "Only the youtube.com cookies are stored.");
        Assert.Equal(AuthStatus.Valid, h.Auth.Status.Status);
        Assert.Null(h.Lease.HeldUntil);
        await h.WaitForAsync(held, ItemState.Completed);

        // It was not valid before (only stored), so the items waiting on auth run again.
        await SchedulerHarness.WaitUntilAsync(
            async () => (await h.ItemEventsAsync(waiting)).Any(e => e.Reason == ItemEventReasons.AuthRestored),
            () => "the waiting item to be requeued");
    }

    [Fact]
    public async Task An_invalid_refresh_jar_is_rejected_and_the_stored_one_stays()
    {
        await using var h = await SchedulerHarness.StartAsync();
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        var before = h.CookieStore.Generations.Select(g => g.Number).ToList();
        var begun = await h.Lease.BeginAsync(Ct);

        var loggedOut = Login("psidts-1").With(Yt("LOGIN_INFO", ""));
        var rejected = await h.Lease.CompleteAsync(begun.LeaseId!, loggedOut, Ct);

        Assert.Equal(RefreshCompleteStatus.Rejected, rejected.Status);
        Assert.Equal(before, h.CookieStore.Generations.Select(g => g.Number));
        Assert.False(h.Lease.IsActive);
        Assert.Equal(AuthStatus.Unknown, h.Auth.Status.Status);

        // A jar that cannot be verified is not kept either.
        var again = await h.Lease.BeginAsync(Ct);
        h.ToolHealth.Ready = false;
        Assert.Equal(RefreshCompleteStatus.Unverified, (await h.Lease.CompleteAsync(again.LeaseId!, Login("psidts-1"), Ct)).Status);
        Assert.Equal(before, h.CookieStore.Generations.Select(g => g.Number));
        Assert.False(h.Lease.IsActive);
    }

    [Fact]
    public async Task One_lease_at_a_time_none_without_cookies_and_none_across_a_new_login()
    {
        await using var h = await SchedulerHarness.StartAsync();
        Assert.Equal(RefreshBeginStatus.NotLoggedIn, (await h.Lease.BeginAsync(Ct)).Status);

        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        var begun = await h.Lease.BeginAsync(Ct);
        Assert.Equal(RefreshBeginStatus.InProgress, (await h.Lease.BeginAsync(Ct)).Status);
        Assert.Equal(RefreshCompleteStatus.NoLease, (await h.Lease.CompleteAsync("not-the-lease", Login("psidts-1"), Ct)).Status);
        Assert.True(h.Lease.IsActive);

        // Logged in again while the browser refreshed the old session: its cookies are not kept.
        var relogin = await h.Auth.SetCookiesAsync(Login("psidts-new"), CookieSource.Login, Ct);
        Assert.Equal(RefreshCompleteStatus.Superseded, (await h.Lease.CompleteAsync(begun.LeaseId!, Login("psidts-1"), Ct)).Status);
        Assert.Equal(relogin.Number, h.CookieStore.Current!.Number);
        Assert.False(h.Lease.IsActive);
    }

    [Fact]
    public async Task Jobs_that_run_without_cookies_are_not_held()
    {
        await using var h = await SchedulerHarness.StartAsync();
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        await h.Auth.SetVerifiedAsync((await h.Auth.CheckOutCurrentAsync(Ct))!, AuthStatus.Expired, cancellationToken: Ct);
        await h.Lease.BeginAsync(Ct);

        Assert.True(h.Lease.IsActive);
        Assert.Null(h.Lease.HeldUntil);
        await h.WaitForAsync(await h.AddAsync("https://fake.test/success?id=leasefree01"), ItemState.Completed);
    }
}
