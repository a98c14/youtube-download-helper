using YoutubeDownloadHelper.Core.Auth;
using static YoutubeDownloadHelper.Core.Tests.Auth.Cookies;

namespace YoutubeDownloadHelper.Core.Tests.Auth;

public sealed class CookieMergeTests
{
    private static readonly DateTimeOffset Later = NextYear.AddMinutes(1);

    private static CookieMergeResult Merge(CookieJar current, CookieJar given, CookieJar written, bool authFailure = false) =>
        CookieMerge.Merge(current, given, written, authFailure, Now);

    [Fact]
    public void A_rotated_cookie_is_merged_with_its_value_and_expiry()
    {
        var jar = Login();
        var written = AsYtDlpWritesIt(jar).With(Yt("SIDCC", "sidcc-1", Later) with { HttpOnly = false });

        var result = Merge(jar, jar, written);

        Assert.Equal(CookieMergeOutcome.Merged, result.Outcome);
        Assert.Equal([new CookieKey(".youtube.com", "/", "SIDCC")], result.Updated);
        Assert.Equal("sidcc-1", result.Jar.Value("SIDCC"));
        Assert.Equal(Later, result.Jar.Find(".youtube.com", "/", "SIDCC")!.Expires);
    }

    [Fact]
    public void The_stored_attributes_are_kept()
    {
        var jar = Login();
        var stored = jar.Find(".youtube.com", "/", "__Secure-1PSIDCC")!;
        var written = jar.With(stored with { Value = "1psidcc-1", HttpOnly = false, Secure = false, IncludeSubdomains = false });

        var merged = Merge(jar, jar, written).Jar.Find(".youtube.com", "/", "__Secure-1PSIDCC")!;

        Assert.Equal(stored with { Value = "1psidcc-1" }, merged);
    }

    [Fact]
    public void Nothing_is_merged_after_an_Auth_failure()
    {
        var jar = Login();
        var written = jar.With(Yt("SIDCC", "sidcc-1"), Yt("__Secure-1PSIDTS", "psidts-x"));

        var result = Merge(jar, jar, written, authFailure: true);

        Assert.Equal(CookieMergeOutcome.RefusedAuthFailure, result.Outcome);
        Assert.Same(jar, result.Jar);
        Assert.Empty(result.Updated);
    }

    [Fact]
    public void An_unchanged_writeback_as_yt_dlp_writes_it_changes_nothing()
    {
        var jar = Login();

        var result = Merge(jar, jar, AsYtDlpWritesIt(jar));

        Assert.Equal(CookieMergeOutcome.NoChanges, result.Outcome);
        Assert.Same(jar, result.Jar);
        Assert.Equal([new CookieMergeIgnored(new CookieKey(".youtube.com", "/", "PREF"), CookieMergeIgnoreReason.Pref)], result.Ignored);
    }

    public static TheoryData<string> CoreNames => [.. CookieDomains.CoreNames];

    [Theory]
    [MemberData(nameof(CoreNames))]
    public void A_core_cookie_is_never_blanked(string name)
    {
        var jar = Login();
        var stored = jar.Find(".youtube.com", "/", name)!;

        foreach (var blank in (string[])["", " "])
        {
            var result = Merge(jar, jar, jar.With(stored with { Value = blank }));

            Assert.Equal(CookieMergeOutcome.NoChanges, result.Outcome);
            Assert.Equal(stored, result.Jar.Find(".youtube.com", "/", name));
            Assert.Equal([new CookieMergeIgnored(stored.Key, CookieMergeIgnoreReason.CoreCookieBlanked)], result.Ignored);
        }
    }

    [Theory]
    [MemberData(nameof(CoreNames))]
    public void A_core_cookie_is_never_deleted(string name)
    {
        var jar = Login();

        var result = Merge(jar, jar, jar.Without(name).With(Yt("SIDCC", "sidcc-1")));

        Assert.Equal(jar.Find(".youtube.com", "/", name), result.Jar.Find(".youtube.com", "/", name));
        Assert.Equal(jar.Count, result.Jar.Count);
    }

    [Fact]
    public void Removals_are_ignored()
    {
        var jar = Login();

        var result = Merge(jar, jar, jar.Without("VISITOR_INFO1_LIVE").Without("SIDCC"));

        Assert.Equal(CookieMergeOutcome.NoChanges, result.Outcome);
        Assert.True(jar.SequenceEqual(result.Jar));
    }

    [Fact]
    public void A_non_core_cookie_may_be_blanked()
    {
        var jar = Login();

        var result = Merge(jar, jar, jar.With(Yt("VISITOR_INFO1_LIVE", "")));

        Assert.Equal("", result.Jar.Value("VISITOR_INFO1_LIVE"));
    }

    [Fact]
    public void A_core_name_off_youtube_is_not_core()
    {
        Assert.False(CookieDomains.IsCore(Yt("SID", "x", domain: ".google.com")));
        Assert.False(CookieDomains.IsCore(Yt("sid", "x")));
        Assert.True(CookieDomains.IsCore(Yt("SID", "x", domain: "WWW.YouTube.com")));
    }

    [Fact]
    public void PREF_is_skipped()
    {
        var jar = Login();

        var result = Merge(jar, jar, jar.With(Yt("PREF", "f6=40000000&tz=UTC&hl=en", Later)));

        Assert.Equal(CookieMergeOutcome.NoChanges, result.Outcome);
        Assert.Equal(jar.Value("PREF"), result.Jar.Value("PREF"));
    }

    [Theory]
    [InlineData(".google.com")]
    [InlineData("accounts.google.com")]
    [InlineData(".google.com.tr")]
    [InlineData(".notyoutube.com")]
    [InlineData(".youtube.com.example.org")]
    [InlineData("youtube.co")]
    public void Cookies_outside_youtube_are_ignored(string domain)
    {
        var jar = Login();

        var result = Merge(jar, jar, jar.With(Yt("SIDCC", "other", Later, domain), Yt("NID", "nid", Later, domain)));

        Assert.Equal(CookieMergeOutcome.NoChanges, result.Outcome);
        Assert.Same(jar, result.Jar);
        Assert.All(result.Ignored, i => Assert.Equal(CookieMergeIgnoreReason.UnrelatedDomain, i.Reason));
        Assert.Equal(2, result.Ignored.Count);
    }

    [Theory]
    [InlineData("youtube.com")]
    [InlineData(".youtube.com")]
    [InlineData("www.youtube.com")]
    [InlineData(".m.YOUTUBE.com")]
    public void Cookies_on_youtube_and_its_subdomains_are_taken(string domain)
    {
        var jar = Login();

        var result = Merge(jar, jar, jar.With(Yt("ST-1", "st", Later, domain)));

        Assert.Equal(CookieMergeOutcome.Merged, result.Outcome);
        Assert.Equal("st", result.Jar.Value("ST-1", domain.ToLowerInvariant()));
    }

    [Fact]
    public void A_job_that_started_before_a_refresh_does_not_write_the_old_PSIDTS_back()
    {
        var given = Login(psidts: "psidts-old");
        var current = Login(psidts: "psidts-new").With(Yt("SIDCC", "sidcc-refresh"));
        var written = AsYtDlpWritesIt(given).With(Yt("__Secure-3PSIDCC", "3psidcc-job", Later));

        var result = Merge(current, given, written);

        Assert.Equal("psidts-new", result.Jar.Value("__Secure-1PSIDTS"));
        Assert.Equal("psidts-new", result.Jar.Value("__Secure-3PSIDTS"));
        Assert.Equal("sidcc-refresh", result.Jar.Value("SIDCC"));
        Assert.Equal("3psidcc-job", result.Jar.Value("__Secure-3PSIDCC"));
        Assert.Equal([new CookieKey(".youtube.com", "/", "__Secure-3PSIDCC")], result.Updated);
    }

    [Fact]
    public void Per_cookie_the_last_writer_wins()
    {
        var given = Login();
        var current = given.With(Yt("SIDCC", "sidcc-other-job"));

        var result = Merge(current, given, given.With(Yt("SIDCC", "sidcc-this-job")));

        Assert.Equal("sidcc-this-job", result.Jar.Value("SIDCC"));
    }

    [Fact]
    public void A_value_saved_as_a_session_cookie_keeps_the_stored_expiry()
    {
        var jar = Login();

        var result = Merge(jar, jar, jar.With(Yt("SIDCC", "sidcc-1") with { Expires = null }));

        var merged = result.Jar.Find(".youtube.com", "/", "SIDCC")!;
        Assert.Equal("sidcc-1", merged.Value);
        Assert.Equal(NextYear, merged.Expires);
    }

    [Fact]
    public void An_expiry_only_change_keeps_the_later_expiry()
    {
        var given = Login();
        var current = given.With(Yt("VISITOR_INFO1_LIVE", "visitor-0", Later.AddDays(1)));

        var extended = Merge(given, given, given.With(Yt("VISITOR_INFO1_LIVE", "visitor-0", Later)));
        var behindARefresh = Merge(current, given, given.With(Yt("VISITOR_INFO1_LIVE", "visitor-0", Later)));

        Assert.Equal(Later, extended.Jar.Find(".youtube.com", "/", "VISITOR_INFO1_LIVE")!.Expires);
        Assert.Equal(CookieMergeOutcome.NoChanges, behindARefresh.Outcome);
        Assert.Same(current, behindARefresh.Jar);
    }

    [Fact]
    public void An_expiry_only_change_keeps_the_current_value()
    {
        var given = Login();
        var current = given.With(Yt("SIDCC", "sidcc-refresh"));

        var result = Merge(current, given, given.With(Yt("SIDCC", "sidcc-0", Later)));

        var merged = result.Jar.Find(".youtube.com", "/", "SIDCC")!;
        Assert.Equal("sidcc-refresh", merged.Value);
        Assert.Equal(Later, merged.Expires);
    }

    [Fact]
    public void A_new_cookie_is_added()
    {
        var jar = Login();

        var result = Merge(jar, jar, jar.With(Yt("ST-xyz", "st", Later)));

        Assert.Equal(jar.Count + 1, result.Jar.Count);
        Assert.Equal("st", result.Jar.Value("ST-xyz"));
    }

    [Fact]
    public void A_cookie_gone_from_the_current_jar_comes_back_with_the_given_attributes()
    {
        var given = Login();
        var current = given.Without("SIDCC");

        var result = Merge(current, given, AsYtDlpWritesIt(given).With(Yt("SIDCC", "sidcc-1") with { HttpOnly = false }));

        Assert.Equal(given.Find(".youtube.com", "/", "SIDCC")! with { Value = "sidcc-1" }, result.Jar.Find(".youtube.com", "/", "SIDCC"));
    }

    [Fact]
    public void An_expired_cookie_is_not_taken()
    {
        var jar = Login();

        var result = Merge(jar, jar, jar.With(Yt("SIDCC", "sidcc-1", Now.AddSeconds(-1)), Yt("ST-old", "st", Now)));

        Assert.Equal(CookieMergeOutcome.NoChanges, result.Outcome);
        Assert.All(result.Ignored, i => Assert.Equal(CookieMergeIgnoreReason.Expired, i.Reason));
        Assert.Equal(2, result.Ignored.Count);
    }

    [Fact]
    public void Names_compare_ordinally()
    {
        var jar = Login();

        // "sid" is a different cookie from "SID"; under tr-TR a culture-aware comparison would also mangle "I".
        var result = Merge(jar, jar, jar.With(Yt("sid", ""), Yt("SİD", "")));

        Assert.Equal(CookieMergeOutcome.Merged, result.Outcome);
        Assert.Equal(jar.Value("SID"), result.Jar.Value("SID"));
        Assert.Equal(jar.Count + 2, result.Jar.Count);
    }
}
