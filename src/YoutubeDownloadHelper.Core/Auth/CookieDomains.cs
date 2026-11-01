using System.Collections.Frozen;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>Which cookies the engine keeps and protects (PLAN.md §5.7, M0 spike <c>docs/spikes/cookie-refresh.md</c>).</summary>
public static class CookieDomains
{
    public const string YouTube = "youtube.com";

    /// <summary>yt-dlp rewrites it with its own locale (<c>hl=en</c>, <c>tz=UTC</c>), so a writeback never takes it.</summary>
    public const string Pref = "PREF";

    /// <summary>
    /// The login cookies on <c>.youtube.com</c> that a writeback may never blank, as confirmed by the M0 spike
    /// (#35). <c>*PSIDRTS</c> (expires minutes after sign-in) and <c>*PSIDCC</c> (changes on every run) are
    /// deliberately not in the list.
    /// </summary>
    public static FrozenSet<string> CoreNames { get; } = FrozenSet.Create(
        StringComparer.Ordinal,
        "SID",
        "HSID",
        "SSID",
        "APISID",
        "SAPISID",
        "__Secure-1PSID",
        "__Secure-3PSID",
        "__Secure-1PAPISID",
        "__Secure-3PAPISID",
        "__Secure-1PSIDTS",
        "__Secure-3PSIDTS",
        "LOGIN_INFO");

    /// <summary><c>youtube.com</c> or a subdomain, with or without the leading dot, ignoring case.</summary>
    public static bool IsYouTube(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var host = domain.AsSpan().TrimStart('.');
        return host.Equals(YouTube, StringComparison.OrdinalIgnoreCase)
            || (host.Length > YouTube.Length
                && host[^(YouTube.Length + 1)] == '.'
                && host.EndsWith(YouTube, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A core login cookie (by exact name) on <c>youtube.com</c> or a subdomain.</summary>
    public static bool IsCore(JarCookie cookie)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        return IsYouTube(cookie.Domain) && CoreNames.Contains(cookie.Name);
    }
}
