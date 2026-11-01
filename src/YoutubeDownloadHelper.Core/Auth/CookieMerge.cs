namespace YoutubeDownloadHelper.Core.Auth;

public enum CookieMergeOutcome
{
    /// <summary>At least one cookie changed; store <see cref="CookieMergeResult.Jar"/> as a new generation.</summary>
    Merged,

    /// <summary>Nothing to take from the writeback; the current jar stays as it is.</summary>
    NoChanges,

    /// <summary>The run ended in an Auth-category failure, so its cookies are not trusted.</summary>
    RefusedAuthFailure,
}

/// <summary>Why a changed cookie in a writeback was not taken.</summary>
public enum CookieMergeIgnoreReason
{
    /// <summary>Not on <c>youtube.com</c>: the engine stores nothing else.</summary>
    UnrelatedDomain,

    /// <summary><c>PREF</c>, which yt-dlp rewrites with its own locale.</summary>
    Pref,

    /// <summary>A core login cookie the writeback would have blanked.</summary>
    CoreCookieBlanked,

    /// <summary>Already expired.</summary>
    Expired,
}

public sealed record CookieMergeIgnored(CookieKey Key, CookieMergeIgnoreReason Reason);

/// <summary>
/// The merged jar (the current one when nothing changed), the cookies that were updated or added, and the changed
/// cookies the guards turned away. Keys only: values are secrets and never leave the jar.
/// </summary>
public sealed record CookieMergeResult(
    CookieMergeOutcome Outcome,
    CookieJar Jar,
    IReadOnlyList<CookieKey> Updated,
    IReadOnlyList<CookieMergeIgnored> Ignored);

/// <summary>
/// The three-way writeback merge (PLAN.md §5.7 point 2). A job is given generation <c>given</c>; when it exits,
/// yt-dlp has rewritten the cookies file (<c>written</c>). Only the cookies that differ from <c>given</c> are applied,
/// and they are applied onto the engine's <c>current</c> jar, which may have moved on (a browser refresh or another
/// job's writeback) while the job ran. Per cookie, the last writer wins.
/// <para>Guards:</para>
/// <list type="bullet">
/// <item>nothing is merged after an Auth-category failure;</item>
/// <item>removals are ignored (yt-dlp drops expired cookies), so no cookie is ever deleted, core ones included;</item>
/// <item>a core login cookie (<see cref="CookieDomains.CoreNames"/>) is never blanked;</item>
/// <item><c>PREF</c> is skipped, and so is everything outside <c>youtube.com</c>;</item>
/// <item>an already-expired cookie is not taken;</item>
/// <item>only value and expiry are taken; the stored attributes stay (yt-dlp loses every HttpOnly flag), and a cookie
/// yt-dlp saved as a session cookie keeps its stored expiry;</item>
/// <item>when only the expiry changed, the later of the stored and written expiry wins, so a job that started
/// before a refresh cannot shorten what the refresh set.</item>
/// </list>
/// Cookie names and domains compare ordinally (domains are lower case).
/// </summary>
public static class CookieMerge
{
    public static CookieMergeResult Merge(
        CookieJar current,
        CookieJar given,
        CookieJar written,
        bool runFailedWithAuth,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(given);
        ArgumentNullException.ThrowIfNull(written);

        if (runFailedWithAuth)
        {
            return new CookieMergeResult(CookieMergeOutcome.RefusedAuthFailure, current, [], []);
        }

        var merged = current.Cookies.ToList();
        var index = merged.Select((c, i) => (c.Key, i)).ToDictionary(x => x.Key, x => x.i);
        var updated = new List<CookieKey>();
        var ignored = new List<CookieMergeIgnored>();

        foreach (var w in written.Cookies)
        {
            given.TryGet(w.Key, out var g);
            var valueChanged = g is null || !string.Equals(g.Value, w.Value, StringComparison.Ordinal);

            // A session cookie in the writeback is yt-dlp's doing, not a real change of expiry.
            var expiryChanged = w.Expires is not null && (g is null || w.Expires != g.Expires);
            if (!valueChanged && !expiryChanged)
            {
                continue;
            }

            if (Guard(w) is { } reason)
            {
                ignored.Add(new CookieMergeIgnored(w.Key, reason));
                continue;
            }

            JarCookie result;
            if (index.TryGetValue(w.Key, out var i))
            {
                var stored = merged[i];
                result = valueChanged
                    ? stored with { Value = w.Value, Expires = w.Expires ?? stored.Expires }
                    : stored with { Expires = Later(stored.Expires, w.Expires) };
                if (result == stored)
                {
                    continue;
                }

                merged[i] = result;
            }
            else
            {
                // New to the current jar: keep the attributes the job was given, if it was given the cookie.
                result = g is null ? w : g with { Value = w.Value, Expires = w.Expires ?? g.Expires };
                index.Add(w.Key, merged.Count);
                merged.Add(result);
            }

            updated.Add(w.Key);
        }

        return updated.Count == 0
            ? new CookieMergeResult(CookieMergeOutcome.NoChanges, current, [], ignored)
            : new CookieMergeResult(CookieMergeOutcome.Merged, new CookieJar(merged), updated, ignored);

        CookieMergeIgnoreReason? Guard(JarCookie w)
        {
            if (!CookieDomains.IsYouTube(w.Domain))
            {
                return CookieMergeIgnoreReason.UnrelatedDomain;
            }

            if (string.Equals(w.Name, CookieDomains.Pref, StringComparison.Ordinal))
            {
                return CookieMergeIgnoreReason.Pref;
            }

            if (CookieDomains.IsCore(w) && string.IsNullOrWhiteSpace(w.Value))
            {
                return CookieMergeIgnoreReason.CoreCookieBlanked;
            }

            return w.IsExpired(now) ? CookieMergeIgnoreReason.Expired : null;
        }
    }

    // null is a session cookie, which never outlasts a persistent one.
    private static DateTimeOffset? Later(DateTimeOffset? stored, DateTimeOffset? written) =>
        stored is null || (written is not null && written > stored) ? written : stored;
}
