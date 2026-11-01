using System.Text.RegularExpressions;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// Removes secrets from log text (PLAN.md §10): the API token wherever it appears, <c>Authorization</c> and cookie
/// header values, bearer/basic credentials, and the value column of Netscape cookie-file lines.
/// </summary>
public sealed partial class LogRedactor(ApiToken token)
{
    public const string Mask = "[redacted]";

    /// <summary>Property names whose values are always masked, whatever they contain.</summary>
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Cookies",
        "Set-Cookie",
        "Token",
        "ApiToken",
        "Password",
    };

    public static bool IsSensitiveName(string name) =>
        SensitiveNames.Contains(name) || SensitiveNames.Contains(name.Replace("_", "-", StringComparison.Ordinal));

    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        text = text.Replace(token.Value, Mask, StringComparison.Ordinal);
        text = HeaderPattern().Replace(text, "$1: " + Mask);
        text = CredentialPattern().Replace(text, "$1 " + Mask);
        text = NetscapeCookiePattern().Replace(text, "$1" + Mask);
        return text;
    }

    // "Authorization: …", "Cookie: …", "Set-Cookie=…", "'Cookie': …" up to the end of the line.
    [GeneratedRegex(@"\b(Proxy-Authorization|Authorization|Set-Cookie|Cookie)['""]?\s*[:=][^\r\n]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();

    // domain, include-subdomains, path, secure, expiry, name, value (tab-separated).
    [GeneratedRegex(@"(?m)^((?:#HttpOnly_)?[^\t\r\n]+\t(?:TRUE|FALSE)\t[^\t\r\n]*\t(?:TRUE|FALSE)\t-?\d+\t[^\t\r\n]*\t)[^\t\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetscapeCookiePattern();
}
