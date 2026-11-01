using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Tests.Persistence;
using static YoutubeDownloadHelper.Core.Tests.Auth.Cookies;

namespace YoutubeDownloadHelper.Core.Tests.Auth;

public sealed class CookieJarStoreTests : IDisposable
{
    private readonly DataPaths _paths = new(Path.Combine(Path.GetTempPath(), "YoutubeDownloadHelper.Tests", $"İndirgeç çerez {Guid.NewGuid():N}"));
    private readonly ManualTimeProvider _time = new(Now);

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    private CookieJarStore Store(ISecretProtector? protector = null) =>
        new(_paths, protector ?? new DpapiSecretProtector(), _time, NullLogger<CookieJarStore>.Instance);

    private static CookieJar Gen(int n) => Login().With(Yt("SIDCC", $"sidcc-{n}"));

    [Fact]
    public void An_empty_store_has_no_current_jar_and_no_file()
    {
        var store = Store();

        Assert.Null(store.Current);
        Assert.Empty(store.Generations);
        Assert.Null(store.RollbackTarget);
        Assert.Null(store.RollBack());
        Assert.False(File.Exists(_paths.CookiesBin));
    }

    [Fact]
    public void Generations_survive_a_restart_with_source_timestamps_and_cookies()
    {
        var store = Store();
        var login = store.Add(Login(), CookieSource.Login, knownGood: true);
        _time.Now += TimeSpan.FromHours(1);
        var writeback = store.Add(Gen(1), CookieSource.Writeback);

        var reloaded = Store().Generations;

        Assert.Equal([writeback.Number, login.Number], reloaded.Select(g => g.Number));
        Assert.Equal((CookieSource.Writeback, Now.AddHours(1), (long?)login.Number, (DateTimeOffset?)null), (reloaded[0].Source, reloaded[0].CreatedAt, reloaded[0].BasedOn, reloaded[0].KnownGoodAt));
        Assert.Equal((CookieSource.Login, Now, (long?)null, (DateTimeOffset?)Now), (reloaded[1].Source, reloaded[1].CreatedAt, reloaded[1].BasedOn, reloaded[1].KnownGoodAt));
        Assert.True(Gen(1).SequenceEqual(reloaded[0].Jar));
        Assert.True(Login().SequenceEqual(reloaded[1].Jar));
    }

    [Fact]
    public void The_file_is_encrypted()
    {
        Store().Add(Login().With(Yt("VALUE", "plaintext-marker-İ")), CookieSource.Login);

        var bytes = File.ReadAllBytes(_paths.CookiesBin);

        foreach (var needle in (string[])["plaintext-marker", "SAPISID", "youtube.com", "Login"])
        {
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)));
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(needle)));
        }
    }

    [Fact]
    public void Only_youtube_cookies_are_stored()
    {
        var jar = Login().With(Yt("SID", "google-sid", domain: ".google.com"), Yt("NID", "nid", domain: ".google.com.tr"));

        var stored = Store().Add(jar, CookieSource.Import);

        Assert.True(Login().SequenceEqual(stored.Jar));
    }

    [Fact]
    public void A_jar_without_youtube_cookies_is_refused()
    {
        var store = Store();

        Assert.Throws<ArgumentException>(() => store.Add(Jar(Yt("SID", "x", domain: ".google.com")), CookieSource.Login));
        Assert.Throws<ArgumentException>(() => store.Add(CookieJar.Empty, CookieSource.Login));
        Assert.Null(store.Current);
    }

    [Fact]
    public void A_rollback_cannot_be_added_directly()
    {
        var store = Store();
        store.Add(Login(), CookieSource.Login);

        Assert.Throws<ArgumentException>(() => store.Add(Login(), CookieSource.Rollback));
    }

    [Theory]
    [InlineData(CookieSource.Writeback)]
    [InlineData(CookieSource.Ping)]
    [InlineData(CookieSource.BrowserRefresh)]
    public void A_derived_jar_needs_a_stored_one(CookieSource source)
    {
        Assert.Throws<InvalidOperationException>(() => Store().Add(Login(), source));
    }

    [Fact]
    public void A_login_or_import_starts_a_new_lineage()
    {
        var store = Store();
        store.Add(Login(), CookieSource.Login);

        var import = store.Add(Login(), CookieSource.Import, basedOn: 1);

        Assert.Null(import.BasedOn);
        Assert.Null(import.RollbackTo);
    }

    [Fact]
    public void Keeps_the_current_generation_and_the_last_3_known_good_ones()
    {
        var store = Store();
        for (var i = 1; i <= 8; i++)
        {
            // 1, 2, 4, 5, 7 and 8 are known-good.
            store.Add(Gen(i), i == 1 ? CookieSource.Login : CookieSource.Writeback, knownGood: i % 3 != 0);
        }

        Assert.Equal([8L, 7, 5, 4], store.Generations.Select(g => g.Number));

        // Not known-good, but current.
        store.Add(Gen(9), CookieSource.Writeback);
        Assert.Equal([9L, 8, 7, 5], store.Generations.Select(g => g.Number));
        Assert.Equal([9L, 8, 7, 5], Store().Generations.Select(g => g.Number));
    }

    [Fact]
    public void A_generation_that_is_not_known_good_is_dropped_once_it_is_no_longer_current()
    {
        var store = Store();
        store.Add(Gen(1), CookieSource.Login);
        store.Add(Gen(2), CookieSource.Import);

        Assert.Equal([2L], store.Generations.Select(g => g.Number));
    }

    [Fact]
    public void Numbers_are_never_reused_across_restarts()
    {
        var store = Store();
        store.Add(Gen(1), CookieSource.Login);
        store.Add(Gen(2), CookieSource.Writeback);
        store.Add(Gen(3), CookieSource.Writeback);

        var next = Store().Add(Gen(4), CookieSource.Writeback);

        Assert.Equal(4, next.Number);
    }

    [Fact]
    public void MarkKnownGood_records_the_time_and_persists()
    {
        var store = Store();
        var login = store.Add(Login(), CookieSource.Login);
        _time.Now += TimeSpan.FromMinutes(5);

        var marked = store.MarkKnownGood(login.Number);

        Assert.Equal(Now.AddMinutes(5), marked!.KnownGoodAt);
        Assert.Equal(Now.AddMinutes(5), Store().Current!.KnownGoodAt);
        Assert.Null(store.MarkKnownGood(42));
    }

    [Fact]
    public void Rollback_restores_the_generation_a_browser_refresh_started_from()
    {
        var store = Store();
        var login = store.Add(Login(psidts: "psidts-0"), CookieSource.Login, knownGood: true);
        _time.Now += TimeSpan.FromHours(24);
        var refresh = store.Add(Login(psidts: "psidts-1"), CookieSource.BrowserRefresh);

        Assert.Equal(login.Number, refresh.BasedOn);
        Assert.Equal(login.Number, refresh.RollbackTo);
        Assert.Equal(login.Number, store.RollbackTarget!.Number);

        _time.Now += TimeSpan.FromMinutes(1);
        var rollback = store.RollBack()!;

        Assert.Equal(CookieSource.Rollback, rollback.Source);
        Assert.Equal(Now.AddHours(24).AddMinutes(1), rollback.CreatedAt);
        Assert.Equal(login.Number, rollback.BasedOn);
        Assert.Null(rollback.KnownGoodAt);
        Assert.Equal("psidts-0", store.Current!.Jar.Value("__Secure-1PSIDTS"));
        Assert.Equal(rollback, store.Current);

        // Anything older than the restored generation holds a dead *PSIDTS: there is no second rollback.
        Assert.Null(rollback.RollbackTo);
        Assert.Null(store.RollBack());
    }

    [Fact]
    public void Writebacks_and_pings_after_a_refresh_keep_its_rollback_target()
    {
        var store = Store();
        var login = store.Add(Gen(0), CookieSource.Login);
        store.Add(Gen(1), CookieSource.BrowserRefresh, knownGood: true);
        for (var i = 2; i <= 6; i++)
        {
            store.Add(Gen(i), i % 2 == 0 ? CookieSource.Writeback : CookieSource.Ping, knownGood: true);
        }

        // The login is neither current nor among the last 3 known-good, but it is the rollback target.
        Assert.Equal([7L, 6, 5, 4, login.Number], store.Generations.Select(g => g.Number));
        Assert.Equal(login.Number, Store().RollbackTarget!.Number);
        Assert.True(Gen(0).SequenceEqual(store.RollBack()!.Jar));
    }

    [Fact]
    public void Only_the_latest_refresh_counts()
    {
        var store = Store();
        store.Add(Gen(0), CookieSource.Login, knownGood: true);
        var first = store.Add(Gen(1), CookieSource.BrowserRefresh, knownGood: true);
        store.Add(Gen(2), CookieSource.BrowserRefresh);

        Assert.Equal(first.Number, store.RollbackTarget!.Number);
    }

    [Fact]
    public void A_refresh_based_on_an_explicit_generation_rolls_back_to_it()
    {
        var store = Store();
        var begin = store.Add(Gen(0), CookieSource.Login, knownGood: true);

        // A job's writeback lands while the refresh runs.
        store.Add(Gen(1), CookieSource.Writeback, knownGood: true);
        var refresh = store.Add(Gen(2), CookieSource.BrowserRefresh, basedOn: begin.Number);

        Assert.Equal(begin.Number, refresh.RollbackTo);
    }

    [Theory]
    [InlineData(CookieSource.Login)]
    [InlineData(CookieSource.Import)]
    public void There_is_no_rollback_without_a_refresh(CookieSource source)
    {
        var store = Store();
        store.Add(Gen(0), source, knownGood: true);
        store.Add(Gen(1), CookieSource.Writeback, knownGood: true);

        Assert.Null(store.RollbackTarget);
        Assert.Null(store.RollBack());
        Assert.Equal(2, store.Current!.Number);
    }

    [Fact]
    public void No_rollback_when_the_refresh_base_is_no_longer_kept()
    {
        var store = Store();
        store.Add(Gen(0), CookieSource.Login);
        store.Add(Gen(1), CookieSource.Writeback);

        var refresh = store.Add(Gen(2), CookieSource.BrowserRefresh, basedOn: 1);

        Assert.Null(refresh.RollbackTo);
        Assert.Null(store.RollBack());
    }

    [Fact]
    public void Clear_deletes_everything_and_numbering_starts_over()
    {
        var store = Store();
        store.Add(Gen(0), CookieSource.Login);
        store.Add(Gen(1), CookieSource.Writeback);

        store.Clear();

        Assert.Null(store.Current);
        Assert.False(File.Exists(_paths.CookiesBin));
        Assert.Null(Store().Current);
        Assert.Equal(1, store.Add(Gen(2), CookieSource.Login).Number);
    }

    [Fact]
    public void Clear_works_on_an_empty_store_and_removes_an_unreadable_file()
    {
        Store().Clear();

        Directory.CreateDirectory(_paths.AuthDir);
        File.WriteAllBytes(_paths.CookiesBin, [1, 2, 3, 4]);
        var store = Store();
        Assert.Null(store.Current);

        store.Clear();

        Assert.Empty(Directory.EnumerateFileSystemEntries(_paths.AuthDir));
    }

    [Fact]
    public void The_default_status_follows_the_stored_jar()
    {
        var store = Store();
        var status = new StoredJarAuthStatusProvider(store);
        Assert.Equal(new AuthInfo(AuthStatus.NotLoggedIn), status.GetStatus());

        store.Add(Login(), CookieSource.Login);
        Assert.Equal(new AuthInfo(AuthStatus.Unknown), status.GetStatus());

        store.Clear();
        Assert.Equal(new AuthInfo(AuthStatus.NotLoggedIn), status.GetStatus());
    }

    [Fact]
    public void A_corrupt_file_is_moved_aside_and_the_store_starts_empty()
    {
        Directory.CreateDirectory(_paths.AuthDir);
        File.WriteAllBytes(_paths.CookiesBin, [1, 2, 3, 4]);

        var store = Store();

        Assert.Null(store.Current);
        Assert.False(File.Exists(_paths.CookiesBin));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(_paths.CookiesBin + ".unreadable"));
        Assert.Equal(1, store.Add(Gen(0), CookieSource.Login).Number);
    }

    [Fact]
    public void A_file_protected_for_another_purpose_or_user_is_unreadable()
    {
        Store(new DpapiSecretProtector("someone else")).Add(Gen(0), CookieSource.Login);

        Assert.Null(Store().Current);
        Assert.True(File.Exists(_paths.CookiesBin + ".unreadable"));
    }

    [Fact]
    public void A_failed_write_changes_nothing()
    {
        var protector = new FailingProtector();
        var store = Store(protector);
        store.Add(Gen(0), CookieSource.Login);

        protector.Fail = true;
        Assert.Throws<IOException>(() => store.Add(Gen(1), CookieSource.Writeback));

        Assert.Equal(1, store.Current!.Number);
        protector.Fail = false;
        Assert.Equal(2, store.Add(Gen(1), CookieSource.Writeback).Number);
    }

    private sealed class FailingProtector : ISecretProtector
    {
        private readonly DpapiSecretProtector _inner = new();

        public bool Fail { get; set; }

        public byte[] Protect(byte[] plaintext) => Fail ? throw new IOException("disk full") : _inner.Protect(plaintext);

        public byte[] Unprotect(byte[] protectedData) => _inner.Unprotect(protectedData);
    }
}
