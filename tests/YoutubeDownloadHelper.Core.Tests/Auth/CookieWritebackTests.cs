using System.Security.AccessControl;
using System.Security.Principal;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Tests.Scheduling;
using static YoutubeDownloadHelper.Core.Tests.Auth.Cookies;

namespace YoutubeDownloadHelper.Core.Tests.Auth;

/// <summary>
/// The per-job cookies file and the writeback merge (PLAN.md §5.7), end to end: the scheduler runs FakeYtDlp, which
/// reads the job's cookies file and saves it back with the changes the URL asks for.
/// </summary>
public sealed class CookieWritebackTests
{
    private static readonly string[] CcNames = ["SIDCC", "__Secure-1PSIDCC", "__Secure-3PSIDCC"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rotated_cookie_is_merged_into_a_new_generation_with_the_stored_attributes()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var login = await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);

        var id = await h.AddAsync("https://fake.test/cookie-writeback?id=cookiewb001");
        await h.WaitForAsync(id, ItemState.Completed);

        var current = h.CookieStore.Current!;
        Assert.Equal((CookieSource.Writeback, (long?)login.Number), (current.Source, current.BasedOn));
        Assert.Equal(["sidcc-0-rotated", "1psidcc-0-rotated", "3psidcc-0-rotated"], CcNames.Select(n => current.Jar.Value(n)));

        // yt-dlp drops every HttpOnly flag and rewrites nothing else: only the rotated values changed.
        var expected = new CookieJar(Login().Cookies.Select(c => CcNames.Contains(c.Name) ? c with { Value = c.Value + "-rotated" } : c));
        Assert.True(expected.SequenceEqual(current.Jar));
        Assert.Equal(0, h.Cookies.Outstanding);
    }

    [Fact]
    public async Task An_auth_failure_merges_nothing_and_deletes_the_file()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var login = await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);

        var id = await h.AddAsync("https://fake.test/bot-check?id=cookiebot01&rotate=SIDCC");
        var item = await h.WaitForAsync(id, ItemState.WaitingAuth);

        Assert.Equal(nameof(ErrorCategory.Auth), item.ErrorCategory);
        Assert.Equal([login.Number], h.CookieStore.Generations.Select(g => g.Number));
        Assert.Equal("sidcc-0", h.CookieStore.Current!.Jar.Value("SIDCC"));
        Assert.True(Directory.Exists(h.Root.Paths.WorkDirFor(id)), "The work folder stays while the item waits.");
        Assert.False(File.Exists(CookiesFileOf(h, id)));
        Assert.Equal(0, h.Cookies.Outstanding);
    }

    [Fact]
    public async Task Another_failure_still_merges_and_the_file_is_removed_from_the_kept_work_folder()
    {
        await using var h = await SchedulerHarness.StartAsync();
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);

        var id = await h.AddAsync("https://fake.test/private?id=cookieprv01&rotate=SIDCC");
        await h.WaitForAsync(id, ItemState.Failed);

        Assert.Equal("sidcc-0-rotated", h.CookieStore.Current!.Jar.Value("SIDCC"));
        Assert.True(Directory.Exists(h.Root.Paths.WorkDirFor(id)));
        Assert.False(File.Exists(CookiesFileOf(h, id)));
    }

    [Fact]
    public async Task A_writeback_never_deletes_or_blanks_a_core_cookie()
    {
        await using var h = await SchedulerHarness.StartAsync();
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);

        var id = await h.AddAsync("https://fake.test/cookie-writeback?id=cookiecor1&drop=SID,LOGIN_INFO,VISITOR_INFO1_LIVE&blank=SAPISID,__Secure-3PSIDTS");
        await h.WaitForAsync(id, ItemState.Completed);

        var jar = h.CookieStore.Current!.Jar;
        Assert.Equal("sidcc-0-rotated", jar.Value("SIDCC"));
        Assert.Equal(
            ["SID-value", "LOGIN_INFO-value", "SAPISID-value", "psidts-0", "visitor-0"],
            ((string[])["SID", "LOGIN_INFO", "SAPISID", "__Secure-3PSIDTS", "VISITOR_INFO1_LIVE"]).Select(n => jar.Value(n)));
        Assert.Equal(Login().Count, jar.Count);
    }

    [Fact]
    public async Task Jobs_run_without_cookies_when_not_logged_in_or_expired()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var item = QueueItem.From(new ItemEntity { Id = 1, Url = "https://fake.test/success", PresetId = "best" });
        Assert.Null(await h.Cookies.BeforeJobAsync(item, h.Root.Paths.WorkDirFor(1), Ct));

        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        h.AuthStatus.Status = AuthStatus.Expired;
        Assert.Null(await h.Cookies.BeforeJobAsync(item, h.Root.Paths.WorkDirFor(1), Ct));
        Assert.Null(await h.Cookies.BeforeResolveAsync(Ct));

        // Members-only content needs the login, so without cookies it waits for auth.
        var id = await h.AddAsync("https://fake.test/members-only?id=cookieexp01");
        await h.WaitForAsync(id, ItemState.WaitingAuth);

        h.AuthStatus.Status = AuthStatus.Valid;
        await h.Actor.RetryAsync(id, Ct);
        await h.WaitForAsync(id, ItemState.Completed);
    }

    [Fact]
    public async Task The_cookies_file_is_the_current_jar_and_readable_only_by_the_user()
    {
        await using var h = await SchedulerHarness.StartAsync();
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);

        var path = (await h.Cookies.BeforeResolveAsync(Ct))!;

        Assert.StartsWith(h.Root.Paths.WorkDir + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(Login().SequenceEqual(NetscapeCookieFile.Parse(await File.ReadAllTextAsync(path, Ct)).Jar));
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        using var identity = WindowsIdentity.GetCurrent();
        var rule = Assert.Single(security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
        Assert.Equal((identity.User, AccessControlType.Allow), ((SecurityIdentifier)rule.IdentityReference, rule.AccessControlType));

        await h.Cookies.AfterResolveAsync(path, failedWithAuth: false, Ct);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Equal(0, h.Cookies.Outstanding);
    }

    [Fact]
    public async Task A_resolve_writeback_merges_unless_it_failed_with_auth()
    {
        await using var h = await SchedulerHarness.StartAsync();
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);

        var refused = (await h.Cookies.BeforeResolveAsync(Ct))!;
        await WriteBackAsync(refused, Login().With(Yt("SIDCC", "sidcc-refused")));
        await h.Cookies.AfterResolveAsync(refused, failedWithAuth: true, Ct);
        Assert.Equal("sidcc-0", h.CookieStore.Current!.Jar.Value("SIDCC"));

        var merged = (await h.Cookies.BeforeResolveAsync(Ct))!;
        await WriteBackAsync(merged, Login().With(Yt("SIDCC", "sidcc-1")));
        await h.Cookies.AfterResolveAsync(merged, failedWithAuth: false, Ct);
        Assert.Equal(("sidcc-1", CookieSource.Writeback), (h.CookieStore.Current!.Jar.Value("SIDCC"), h.CookieStore.Current.Source));
        Assert.False(File.Exists(refused) || File.Exists(merged));
    }

    [Fact]
    public async Task Concurrent_writebacks_each_apply_their_own_changes_onto_the_current_jar()
    {
        await using var h = await SchedulerHarness.StartAsync();
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        var first = (await h.Cookies.BeforeResolveAsync(Ct))!;
        var second = (await h.Cookies.BeforeResolveAsync(Ct))!;

        await WriteBackAsync(first, Login().With(Yt("SIDCC", "sidcc-first")));
        await WriteBackAsync(second, Login().With(Yt("__Secure-1PSIDCC", "1psidcc-second")));
        await Task.WhenAll(
            h.Cookies.AfterResolveAsync(first, failedWithAuth: false, Ct),
            h.Cookies.AfterResolveAsync(second, failedWithAuth: false, Ct));

        var jar = h.CookieStore.Current!.Jar;
        Assert.Equal(("sidcc-first", "1psidcc-second"), (jar.Value("SIDCC"), jar.Value("__Secure-1PSIDCC")));
    }

    [Fact]
    public async Task A_writeback_from_before_a_log_out_or_a_new_login_is_dropped()
    {
        await using var h = await SchedulerHarness.StartAsync();
        await h.Auth.SetCookiesAsync(Login(), CookieSource.Login, Ct);
        var beforeLogout = (await h.Cookies.BeforeResolveAsync(Ct))!;

        await h.Auth.LogOutAsync(Ct);
        var relogin = await h.Auth.SetCookiesAsync(Login("psidts-new"), CookieSource.Login, Ct);
        await WriteBackAsync(beforeLogout, Login().With(Yt("SIDCC", "sidcc-old-session")));
        await h.Cookies.AfterResolveAsync(beforeLogout, failedWithAuth: false, Ct);

        Assert.Equal([relogin.Number], h.CookieStore.Generations.Select(g => g.Number));
        Assert.Equal("sidcc-0", h.CookieStore.Current!.Jar.Value("SIDCC"));
        Assert.False(File.Exists(beforeLogout));
    }

    [Fact]
    public async Task Login_and_log_out_publish_auth_changed_and_a_login_requeues_items_waiting_on_auth()
    {
        await using var h = await SchedulerHarness.StartAsync();
        var id = await h.AddAsync("https://fake.test/members-only?id=cookiewai01");
        await h.WaitForAsync(id, ItemState.WaitingAuth);
        h.DrainEvents();

        await h.Auth.SetCookiesAsync(Login(), CookieSource.Import, Ct);
        await h.WaitForAsync(id, ItemState.Completed);
        await h.Auth.LogOutAsync(Ct);

        var changes = h.DrainEvents().OfType<AuthChangedEvent>().Select(e => e.Auth.Status);
        Assert.Equal([AuthStatus.Unknown, AuthStatus.NotLoggedIn], changes);
        Assert.Null(h.CookieStore.Current);
        Assert.False(File.Exists(h.Root.Paths.CookiesBin));
    }

    [Fact]
    public async Task Only_a_login_or_an_import_replaces_the_cookies()
    {
        await using var h = await SchedulerHarness.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Auth.SetCookiesAsync(Login(), CookieSource.Writeback, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Auth.SetCookiesAsync(Jar(Yt("SID", "x", domain: ".google.com")), CookieSource.Login, Ct));
        Assert.Null(h.CookieStore.Current);
        Assert.DoesNotContain(h.DrainEvents(), e => e is AuthChangedEvent);
    }

    private static string CookiesFileOf(SchedulerHarness h, long itemId) =>
        Path.Combine(h.Root.Paths.WorkDirFor(itemId), CookieFileProvider.FileName);

    // As yt-dlp saves it: no HttpOnly flags.
    private static Task WriteBackAsync(string path, CookieJar jar) =>
        File.WriteAllTextAsync(path, NetscapeCookieFile.Write(AsYtDlpWritesIt(jar)), Ct);
}
