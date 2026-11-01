using System.Globalization;
using System.Text;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>The cookies read from a Netscape file, and the 1-based numbers of the lines that were skipped as invalid.</summary>
public sealed record NetscapeParseResult(CookieJar Jar, IReadOnlyList<int> SkippedLines);

/// <summary>
/// Reads and writes Netscape <c>cookies.txt</c>, the format yt-dlp's <c>--cookies</c> uses (Python's
/// <c>MozillaCookieJar</c>) and browser extensions export. One cookie per line, seven tab-separated fields:
/// domain, include-subdomains flag, path, secure flag, expiry (Unix seconds, <c>0</c> or empty for a session
/// cookie), name, value. A <c>#HttpOnly_</c> prefix on the domain marks an HttpOnly cookie; other lines starting
/// with <c>#</c> are comments.
/// <para>
/// Parsing is lenient like yt-dlp's: an invalid line is skipped and reported by number (never by content, which
/// holds a secret). A line with six fields is a cookie with an empty value (some exporters drop the trailing tab).
/// </para>
/// </summary>
public static class NetscapeCookieFile
{
    public const string Header = "# Netscape HTTP Cookie File";

    public const string HttpOnlyPrefix = "#HttpOnly_";

    private const string True = "TRUE";
    private const string False = "FALSE";

    // The range DateTimeOffset.FromUnixTimeSeconds accepts.
    private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    public static NetscapeParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var cookies = new List<JarCookie>();
        var skipped = new List<int>();
        var lineNumber = 0;
        foreach (var rawLine in Lines(text.TrimStart('\uFEFF')))
        {
            lineNumber++;
            var line = rawLine;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var httpOnly = false;
            if (line.StartsWith(HttpOnlyPrefix, StringComparison.Ordinal))
            {
                httpOnly = true;
                line = line[HttpOnlyPrefix.Length..];
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }

            if (TryParseLine(line, httpOnly) is { } cookie)
            {
                cookies.Add(cookie);
            }
            else
            {
                skipped.Add(lineNumber);
            }
        }

        return new NetscapeParseResult(new CookieJar(cookies), skipped);
    }

    /// <summary>The jar as a cookies file: the header, then one line per cookie, <c>\n</c> line endings.</summary>
    public static string Write(CookieJar jar)
    {
        ArgumentNullException.ThrowIfNull(jar);

        var sb = new StringBuilder(Header).Append('\n').Append('\n');
        foreach (var c in jar.Cookies)
        {
            if (c.HttpOnly)
            {
                sb.Append(HttpOnlyPrefix);
            }

            sb.Append(c.Domain).Append('\t')
                .Append(c.IncludeSubdomains ? True : False).Append('\t')
                .Append(c.Path).Append('\t')
                .Append(c.Secure ? True : False).Append('\t')
                .Append(c.Expires is { } expires ? expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : "0").Append('\t')
                .Append(c.Name).Append('\t')
                .Append(c.Value).Append('\n');
        }

        return sb.ToString();
    }

    // Only CR, LF and CRLF end a line, as in Python's universal newlines. string.Split and EnumerateLines would also
    // split on U+2028, U+0085 and form feeds, which a cookie value may contain.
    private static IEnumerable<string> Lines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n')
            {
                yield return text[start..i];
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private static JarCookie? TryParseLine(string line, bool httpOnly)
    {
        var fields = line.Split('\t');
        if (fields.Length is not (6 or 7))
        {
            return null;
        }

        var (domain, path, name) = (fields[0].Trim(), fields[2], fields[5]);
        if (domain.Length == 0
            || path.Length == 0
            || name.Length == 0
            || !TryParseFlag(fields[1], out var includeSubdomains)
            || !TryParseFlag(fields[3], out var secure)
            || !TryParseExpiry(fields[4], out var expires))
        {
            return null;
        }

        return new JarCookie(domain, path, name, fields.Length == 7 ? fields[6] : "")
        {
            IncludeSubdomains = includeSubdomains,
            Secure = secure,
            HttpOnly = httpOnly,
            Expires = expires,
        };
    }

    private static bool TryParseFlag(string field, out bool value)
    {
        var span = field.AsSpan().Trim();
        value = span.Equals(True, StringComparison.OrdinalIgnoreCase);
        return value || span.Equals(False, StringComparison.OrdinalIgnoreCase);
    }

    // Empty or 0: a session cookie. Some exporters write fractional seconds; the fraction is dropped.
    private static bool TryParseExpiry(string field, out DateTimeOffset? expires)
    {
        expires = null;
        var span = field.AsSpan().Trim();
        if (span.IsEmpty)
        {
            return true;
        }

        long seconds;
        if (long.TryParse(span, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
        {
            seconds = whole;
        }
        else if (double.TryParse(span, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var fractional)
            && fractional >= MinUnixSeconds
            && fractional <= MaxUnixSeconds)
        {
            seconds = (long)Math.Truncate(fractional);
        }
        else
        {
            return false;
        }

        if (seconds < MinUnixSeconds || seconds > MaxUnixSeconds)
        {
            return false;
        }

        expires = seconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}
