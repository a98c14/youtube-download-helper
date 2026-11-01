using YoutubeDownloadHelper.Core.Auth;
using static YoutubeDownloadHelper.Core.Tests.Auth.Cookies;

namespace YoutubeDownloadHelper.Core.Tests.Auth;

public sealed class NetscapeCookieFileTests
{
    // Shaped like a browser-extension export: header, comments, HttpOnly lines, session cookies, CRLF.
    private const string Export =
        "# Netscape HTTP Cookie File\r\n" +
        "# https://curl.se/docs/http-cookies.html\r\n" +
        "\r\n" +
        "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1824984000\t__Secure-3PSID\tg.a000psid\r\n" +
        ".youtube.com\tTRUE\t/\tTRUE\t1824984000\tSAPISID\tAbC/dEf-123\r\n" +
        "www.youtube.com\tFALSE\t/feed\tFALSE\t0\tST-abc\tsession=1\r\n" +
        ".youtube.com\tTRUE\t/\tFALSE\t\tYSC\tempty-expiry\r\n";

    [Fact]
    public void Parses_fields_HttpOnly_prefix_and_session_cookies()
    {
        var result = NetscapeCookieFile.Parse(Export);

        Assert.Empty(result.SkippedLines);
        Assert.Equal(
            [
                new JarCookie(".youtube.com", "/", "__Secure-3PSID", "g.a000psid")
                {
                    IncludeSubdomains = true, Secure = true, HttpOnly = true, Expires = DateTimeOffset.FromUnixTimeSeconds(1824984000),
                },
                new JarCookie(".youtube.com", "/", "SAPISID", "AbC/dEf-123")
                {
                    IncludeSubdomains = true, Secure = true, Expires = DateTimeOffset.FromUnixTimeSeconds(1824984000),
                },
                new JarCookie("www.youtube.com", "/feed", "ST-abc", "session=1"),
                new JarCookie(".youtube.com", "/", "YSC", "empty-expiry") { IncludeSubdomains = true },
            ],
            result.Jar.Cookies);
    }

    [Fact]
    public void Round_trips_through_write_and_parse()
    {
        var jar = Jar(
            Yt("__Secure-1PSIDTS", "sidts-1"),
            Yt("PREF", "f6=40000000&tz=Europe.Istanbul", expires: null) with { HttpOnly = false, Secure = false },
            Yt("LOGIN_INFO", "AFmmF2swRQIhAP:QUQ3MjNm=="),
            new JarCookie("youtube.com", "/watch", "host-only", "v") { Expires = NextYear });

        var text = NetscapeCookieFile.Write(jar);
        var back = NetscapeCookieFile.Parse(text);

        Assert.Empty(back.SkippedLines);
        Assert.True(jar.SequenceEqual(back.Jar));
        Assert.Equal(text, NetscapeCookieFile.Write(back.Jar));
    }

    [Fact]
    public void Writes_the_header_HttpOnly_prefix_and_0_for_session_cookies()
    {
        var jar = Jar(
            new JarCookie(".youtube.com", "/", "SID", "s") { IncludeSubdomains = true, Secure = true, HttpOnly = true, Expires = DateTimeOffset.FromUnixTimeSeconds(1824984000) },
            new JarCookie("www.youtube.com", "/", "YSC", "y"));

        Assert.Equal(
            "# Netscape HTTP Cookie File\n\n" +
            "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1824984000\tSID\ts\n" +
            "www.youtube.com\tFALSE\t/\tFALSE\t0\tYSC\ty\n",
            NetscapeCookieFile.Write(jar));
    }

    [Theory]
    [InlineData("İstanbul ılık ğüşöç")]
    [InlineData("emoji 🎬 and 中文")]
    [InlineData("line\u2028separator\u0085next\fform-feed")]
    [InlineData("quotes \"a\" 'b' and = ; ,")]
    public void Keeps_Unicode_and_unusual_values_intact(string value)
    {
        var jar = Jar(Yt("VALUE", value));

        var back = NetscapeCookieFile.Parse(NetscapeCookieFile.Write(jar));

        Assert.Empty(back.SkippedLines);
        Assert.Equal(value, back.Jar.Value("VALUE"));
    }

    [Fact]
    public void Ignores_a_byte_order_mark_and_mixed_line_endings()
    {
        var text = "\uFEFF# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tA\t1\r.youtube.com\tTRUE\t/\tTRUE\t0\tB\t2\r\n.youtube.com\tTRUE\t/\tTRUE\t0\tC\t3";

        var result = NetscapeCookieFile.Parse(text);

        Assert.Empty(result.SkippedLines);
        Assert.Equal(["A", "B", "C"], result.Jar.Cookies.Select(c => c.Name));
    }

    [Fact]
    public void A_line_with_six_fields_is_a_cookie_with_an_empty_value()
    {
        var result = NetscapeCookieFile.Parse(".youtube.com\tTRUE\t/\tTRUE\t1824984000\tEMPTY");

        Assert.Empty(result.SkippedLines);
        Assert.Equal("", result.Jar.Value("EMPTY"));
    }

    [Fact]
    public void Fractional_expiry_is_truncated_to_seconds()
    {
        var result = NetscapeCookieFile.Parse(".youtube.com\tTRUE\t/\tTRUE\t1824984000.75\tA\t1");

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1824984000), result.Jar.Cookies.Single().Expires);
    }

    [Fact]
    public void Skips_invalid_lines_and_reports_their_numbers()
    {
        var text = string.Join('\n',
            "# Netscape HTTP Cookie File",                         // 1
            ".youtube.com\tTRUE\t/\tTRUE\t0\tGOOD\t1",             // 2
            ".youtube.com\tTRUE\t/\tTRUE\t0",                      // 3: too few fields
            ".youtube.com\tTRUE\t/\tTRUE\t0\tA\tb\tc",             // 4: too many fields
            ".youtube.com\tYES\t/\tTRUE\t0\tA\t1",                 // 5: bad flag
            ".youtube.com\tTRUE\t/\tNO\t0\tA\t1",                  // 6: bad flag
            ".youtube.com\tTRUE\t/\tTRUE\tsoon\tA\t1",             // 7: bad expiry
            ".youtube.com\tTRUE\t/\tTRUE\t99999999999999\tA\t1",   // 8: expiry out of range
            ".youtube.com\tTRUE\t/\tTRUE\t0\t\tnameless",          // 9: no name
            "\tTRUE\t/\tTRUE\t0\tA\t1",                            // 10: no domain
            ".youtube.com\tTRUE\t\tTRUE\t0\tA\t1",                 // 11: no path
            "   ",                                                  // 12: blank
            "just some text",                                       // 13
            ".youtube.com\tTRUE\t/\tTRUE\t0\tALSO_GOOD\t2");        // 14

        var result = NetscapeCookieFile.Parse(text);

        Assert.Equal([3, 4, 5, 6, 7, 8, 9, 10, 11, 13], result.SkippedLines);
        Assert.Equal(["GOOD", "ALSO_GOOD"], result.Jar.Cookies.Select(c => c.Name));
    }

    [Fact]
    public void A_repeated_cookie_keeps_its_first_position_and_last_value()
    {
        var result = NetscapeCookieFile.Parse(string.Join('\n',
            ".youtube.com\tTRUE\t/\tFALSE\t0\tVISITOR_INFO1_LIVE\tfirst",
            ".youtube.com\tTRUE\t/\tFALSE\t0\tYSC\ty",
            ".youtube.com\tTRUE\t/\tFALSE\t0\tVISITOR_INFO1_LIVE\tsecond",
            ".youtube.com\tTRUE\t/other\tFALSE\t0\tVISITOR_INFO1_LIVE\tother-path"));

        Assert.Equal(
            [("VISITOR_INFO1_LIVE", "/", "second"), ("YSC", "/", "y"), ("VISITOR_INFO1_LIVE", "/other", "other-path")],
            result.Jar.Cookies.Select(c => (c.Name, c.Path, c.Value)));
    }

    // Under tr-TR, culture-aware case mapping turns "I" into dotless "ı" and "i" into "İ".
    [Fact]
    public void Flags_and_domains_ignore_case_without_the_current_culture()
    {
        var result = NetscapeCookieFile.Parse("#HttpOnly_.WWW.YOUTUBE.COM\ttrue\t/\tTrue\t0\tLOGIN_INFO\tx");

        var cookie = Assert.Single(result.Jar.Cookies);
        Assert.Equal(".www.youtube.com", cookie.Domain);
        Assert.True(cookie.IncludeSubdomains);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.True(CookieDomains.IsYouTube(cookie.Domain));
        Assert.NotNull(result.Jar.Find(".WWW.YouTube.COM", "/", "LOGIN_INFO"));
    }

    [Theory]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void A_cookie_cannot_hold_a_tab_or_line_break_and_the_error_does_not_show_the_value(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => new JarCookie(".youtube.com", "/", "SID", value));

        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }
}
