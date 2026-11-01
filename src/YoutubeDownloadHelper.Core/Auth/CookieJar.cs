using System.Diagnostics.CodeAnalysis;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// An immutable set of cookies, one per <see cref="CookieKey"/>, in the order they were first seen. When a key
/// repeats, the later cookie replaces the earlier one in place (browser exports can list a cookie twice).
/// </summary>
public sealed class CookieJar
{
    private readonly List<JarCookie> _cookies = [];
    private readonly Dictionary<CookieKey, int> _index = [];

    public CookieJar(IEnumerable<JarCookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        foreach (var cookie in cookies)
        {
            ArgumentNullException.ThrowIfNull(cookie);
            if (_index.TryGetValue(cookie.Key, out var i))
            {
                _cookies[i] = cookie;
            }
            else
            {
                _index.Add(cookie.Key, _cookies.Count);
                _cookies.Add(cookie);
            }
        }
    }

    public static CookieJar Empty { get; } = new([]);

    public IReadOnlyList<JarCookie> Cookies => _cookies;

    public int Count => _cookies.Count;

    public bool TryGet(CookieKey key, [NotNullWhen(true)] out JarCookie? cookie)
    {
        key = key with { Domain = key.Domain.ToLowerInvariant() };
        if (_index.TryGetValue(key, out var i))
        {
            cookie = _cookies[i];
            return true;
        }

        cookie = null;
        return false;
    }

    public JarCookie? Find(string domain, string path, string name) => TryGet(new(domain, path, name), out var c) ? c : null;

    /// <summary>The cookies on <c>youtube.com</c> and its subdomains (PLAN.md §5.7: nothing else is stored).</summary>
    public CookieJar YouTubeOnly() => new(_cookies.Where(c => CookieDomains.IsYouTube(c.Domain)));

    /// <summary>Same cookies (keys, values and attributes) in the same order.</summary>
    public bool SequenceEqual(CookieJar other) => _cookies.SequenceEqual(other._cookies);
}
