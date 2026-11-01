using YoutubeDownloadHelper.Core.Auth;

namespace YoutubeDownloadHelper.Core.Tests.Auth;

/// <summary>Cookie builders for the auth tests. Values are made up; nothing here is a real session.</summary>
internal static class Cookies
{
    public static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset NextYear = Now.AddYears(1);

    public static JarCookie Yt(string name, string value, DateTimeOffset? expires = null, string domain = ".youtube.com") =>
        new(domain, "/", name, value)
        {
            IncludeSubdomains = domain.StartsWith('.'),
            Secure = true,
            HttpOnly = name.StartsWith("__Secure-", StringComparison.Ordinal) || name is "SID" or "HSID" or "SSID",
            Expires = expires ?? NextYear,
        };

    public static CookieJar Jar(params JarCookie[] cookies) => new(cookies);

    /// <summary>A jar like a fresh login's: the 12 core cookies, the <c>*CC</c> cookies, PREF and the visitor cookies.</summary>
    public static CookieJar Login(string psidts = "psidts-0") => Jar(
        [
            .. CookieDomains.CoreNames.Order(StringComparer.Ordinal).Select(n => Yt(n, n.EndsWith("PSIDTS", StringComparison.Ordinal) ? psidts : $"{n}-value")),
            Yt("SIDCC", "sidcc-0"),
            Yt("__Secure-1PSIDCC", "1psidcc-0"),
            Yt("__Secure-3PSIDCC", "3psidcc-0"),
            Yt("PREF", "f6=40000000&tz=Europe.Istanbul"),
            Yt("VISITOR_INFO1_LIVE", "visitor-0"),
        ]);

    /// <summary>What yt-dlp writes back: no HttpOnly flags, PREF as a session cookie with its own locale.</summary>
    public static CookieJar AsYtDlpWritesIt(CookieJar jar) => new(jar.Cookies.Select(c => c.Name == "PREF"
        ? c with { HttpOnly = false, Value = c.Value + "&hl=en", Expires = null }
        : c with { HttpOnly = false }));

    public static CookieJar With(this CookieJar jar, params JarCookie[] changes) => new([.. jar.Cookies, .. changes]);

    public static CookieJar Without(this CookieJar jar, string name) => new(jar.Cookies.Where(c => c.Name != name));

    public static string Value(this CookieJar jar, string name, string domain = ".youtube.com") => jar.Find(domain, "/", name)!.Value;
}
