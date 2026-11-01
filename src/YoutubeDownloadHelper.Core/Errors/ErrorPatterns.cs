using System.Globalization;
using System.Text.RegularExpressions;
using YoutubeDownloadHelper.Contracts.Errors;

namespace YoutubeDownloadHelper.Core.Errors;

/// <summary>Which output lines a pattern looks at.</summary>
internal enum LineScope
{
    /// <summary><c>ERROR:</c> lines only.</summary>
    Errors,

    /// <summary>Every line, including <c>WARNING:</c> lines and the usage error.</summary>
    All,
}

/// <summary>Matches one line; on a match, adds the message args it found and returns true.</summary>
internal delegate bool LineMatcher(string line, Dictionary<string, string> args);

internal sealed record ErrorPattern(
    string Code,
    ErrorCategory Category,
    LineScope Scope,
    LineMatcher Matches,
    Func<IReadOnlyDictionary<string, string>, string> Message);

/// <summary>
/// Every pattern the <see cref="ErrorClassifier"/> uses (PLAN.md §5.2), in priority order: the first pattern that
/// matches any of its lines wins. Matching is ordinal and case-insensitive. The fixtures in
/// <c>tests/YoutubeDownloadHelper.Core.Tests/Errors/Fixtures</c> cover each code; add one with every new pattern.
/// </summary>
internal static partial class ErrorPatterns
{
    public static IReadOnlyList<ErrorPattern> Patterns { get; } =
    [
        // Local I/O first: yt-dlp wraps OS errors in messages ("unable to write data", "Unable to rename file") that
        // the other patterns must not claim. On Windows, Python reports ERROR_DISK_FULL as errno 28.
        new(ErrorCodes.DiskFull, ErrorCategory.LocalIo, LineScope.Errors,
            Any("[Errno 28]", "No space left on device", "[WinError 112]", "not enough space on the disk"),
            _ => "The disk is full. Free up space or choose another download folder."),
        new(ErrorCodes.FileInUse, ErrorCategory.LocalIo, LineScope.Errors,
            WithPath("[WinError 32]", "being used by another process"),
            a => $"Another program is using the file{InPath(a)}. Close it and retry."),
        new(ErrorCodes.AccessDenied, ErrorCategory.LocalIo, LineScope.Errors,
            WithPath("[Errno 13]", "[WinError 5]", "Permission denied", "Access is denied"),
            a => $"Windows denied access{InPath(a)}. Choose another download folder or check its permissions."),

        // Errno 22 is what Python raises for a file name longer than 255 characters; yt-dlp sanitizes the other
        // causes (reserved characters) out of its file names.
        new(ErrorCodes.PathTooLong, ErrorCategory.LocalIo, LineScope.Errors,
            WithPath("[WinError 206]", "[Errno 36]", "[Errno 22] Invalid argument", "File name too long", "filename or extension is too long"),
            a => $"The file path is too long{InPath(a)}. Choose a shorter download folder or file name template."),
        new(ErrorCodes.PathNotFound, ErrorCategory.LocalIo, LineScope.Errors,
            WithPath("[WinError 3]", "[WinError 2]", "[Errno 2]", "cannot find the path specified"),
            a => $"The folder does not exist or cannot be created{InPath(a)}. Choose another download folder."),

        // YouTube's session rate limit arrives as "Video unavailable. This content isn't available, try again later",
        // so it must come before the unavailable patterns.
        new(ErrorCodes.RateLimited, ErrorCategory.Transient, LineScope.Errors,
            Any("try again later", "rate-limited", "rate limited"),
            _ => "YouTube is temporarily limiting requests from this connection."),

        // Auth. The bot check matches both apostrophes ("you’re" and "you're").
        new(ErrorCodes.BotCheck, ErrorCategory.Auth, LineScope.Errors,
            Any("confirm you’re not a bot", "confirm you're not a bot", "confirm that you're not a bot"),
            _ => "YouTube wants to confirm this is not a bot. Logging in again usually fixes it."),
        new(ErrorCodes.AgeRestricted, ErrorCategory.Auth, LineScope.Errors,
            Any("confirm your age", "age-restricted", "age restricted"),
            _ => "This video is age-restricted. Log in with an account that can watch it."),
        new(ErrorCodes.MembersOnly, ErrorCategory.Auth, LineScope.Errors,
            Any("members-only", "Join this channel", "channel's members"),
            _ => "This video is for channel members only. Log in with an account that is a member."),
        new(ErrorCodes.PremiumOnly, ErrorCategory.Auth, LineScope.Errors,
            Any("Premium members", "requires YouTube Premium", "Music Premium"),
            _ => "This video needs YouTube Premium. Log in with a Premium account."),
        new(ErrorCodes.LoginRequired, ErrorCategory.Auth, LineScope.Errors,
            Any("Login details are needed", "Login required", "account cookies are no longer valid", "This video requires login"),
            _ => "YouTube needs you to log in to download this."),

        // Unavailable. "Private video. Sign in if you've been granted access" mentions signing in but is not
        // something a login fixes, and no auth pattern above matches it. Copyright and geo-blocking come before
        // "Video unavailable", which prefixes them.
        new(ErrorCodes.Private, ErrorCategory.Unavailable, LineScope.Errors,
            Any("Private video", "This video is private"),
            _ => "This video is private."),
        new(ErrorCodes.Copyright, ErrorCategory.Unavailable, LineScope.Errors,
            Any("copyright"),
            _ => "This video was taken down because of a copyright claim."),
        new(ErrorCodes.GeoBlocked, ErrorCategory.Unavailable, LineScope.Errors,
            Any("available in your country", "not available from your location", "geo restricted", "geo-restricted"),
            _ => "This video is not available in your country."),
        new(ErrorCodes.Upcoming, ErrorCategory.Unavailable, LineScope.Errors,
            Any("This live event will begin", "Premieres in", "Premiere will begin"),
            _ => "This live stream or premiere has not started yet."),
        new(ErrorCodes.Removed, ErrorCategory.Unavailable, LineScope.Errors,
            Any("Video unavailable", "video is unavailable", "has been removed", "no longer available", "has been terminated"),
            _ => "This video has been removed or is no longer available."),
        new(ErrorCodes.PlaylistNotFound, ErrorCategory.Unavailable, LineScope.Errors,
            Any("playlist does not exist", "This channel does not exist"),
            _ => "This playlist or channel does not exist."),
        new(ErrorCodes.UnsupportedUrl, ErrorCategory.Unavailable, LineScope.Errors,
            Capture(UnsupportedUrlRegex()),
            a => $"This is not a link yt-dlp can download: {a.GetValueOrDefault("url")}"),

        // Broken tools. The signature and JS runtime problems show up as warnings, followed by an error that only
        // says formats are missing ("Requested format is not available"), so these look at every line. A missing
        // JS runtime also causes 403s, so they come before the HTTP patterns.
        new(ErrorCodes.JsRuntimeMissing, ErrorCategory.ToolBroken, LineScope.All,
            Any("No supported JavaScript runtime"),
            _ => "yt-dlp could not find its JavaScript runtime (Deno). Repairing the tools fixes this."),
        new(ErrorCodes.Signature, ErrorCategory.ToolBroken, LineScope.All,
            Any("nsig extraction failed", "Signature extraction failed", "challenge solving failed", "Unable to decode n-parameter"),
            _ => "yt-dlp could not decode YouTube's video signature. A yt-dlp update usually fixes this."),
        new(ErrorCodes.FfmpegMissing, ErrorCategory.ToolBroken, LineScope.Errors,
            Any("ffmpeg not found", "ffmpeg is not installed", "ffprobe and ffmpeg not found"),
            _ => "ffmpeg is missing. Repairing the tools fixes this."),
        new(ErrorCodes.BadOptions, ErrorCategory.ToolBroken, LineScope.All,
            Capture(UsageErrorRegex()),
            _ => "yt-dlp did not accept the command line. A yt-dlp update usually fixes this."),

        // Transient.
        new(ErrorCodes.Fragment, ErrorCategory.Transient, LineScope.Errors,
            Capture(FragmentRegex()),
            _ => "Part of the video could not be downloaded."),
        new(ErrorCodes.RateLimited, ErrorCategory.Transient, LineScope.Errors,
            HttpStatus(s => s == 429),
            _ => "YouTube is temporarily limiting requests from this connection."),
        new(ErrorCodes.ServerError, ErrorCategory.Transient, LineScope.Errors,
            HttpStatus(s => s is >= 500 and <= 599, withStatus: true),
            a => $"The server had a temporary problem (HTTP {a.GetValueOrDefault("status")})."),
        new(ErrorCodes.Forbidden, ErrorCategory.Transient, LineScope.Errors,
            HttpStatus(s => s == 403),
            _ => "YouTube refused the download (HTTP 403)."),
        new(ErrorCodes.NotFound, ErrorCategory.Unavailable, LineScope.Errors,
            HttpStatus(s => s == 404),
            _ => "The video was not found (HTTP 404)."),

        // Network errors also end with yt-dlp's "please report this issue" text, so they come before the extractor
        // pattern below.
        new(ErrorCodes.Network, ErrorCategory.Transient, LineScope.Errors,
            Any(
                "getaddrinfo failed", "Failed to resolve", "Temporary failure in name resolution", "timed out",
                "Connection reset", "ConnectionResetError", "Connection aborted", "Connection refused", "actively refused",
                "Failed to establish a new connection", "Unable to connect to proxy", "Remote end closed connection",
                "IncompleteRead", "Network is unreachable", "No route to host", "[WinError 10054]", "[WinError 10060]",
                "[WinError 10061]", "SSL:", "EOF occurred in violation of protocol"),
            _ => "The connection to YouTube failed."),

        // yt-dlp asks for a bug report when an extractor hits something it does not understand, which after the
        // patterns above usually means YouTube changed and yt-dlp needs an update.
        new(ErrorCodes.Extractor, ErrorCategory.ToolBroken, LineScope.Errors,
            Any("Unable to extract", "please report this issue", "Failed to parse JSON", "The following content is not available on this app"),
            _ => "yt-dlp could not read YouTube's page. A yt-dlp update usually fixes this."),
    ];

    /// <summary>
    /// Exit codes that are Windows status codes (negative as an <see cref="int"/>, e.g. <c>0xC0000135</c>, a missing
    /// DLL) mean yt-dlp.exe crashed or never started, whatever it printed.
    /// </summary>
    public static bool IsCrash(int exitCode) => exitCode < 0;

    public static string CrashMessage(string hexExitCode) =>
        $"yt-dlp crashed (exit code {hexExitCode}). Repairing the tools may fix this.";

    public static string UnknownMessage(int exitCode) =>
        $"yt-dlp failed (exit code {exitCode.ToString(CultureInfo.InvariantCulture)}). See the details for its output.";

    private static LineMatcher Any(params string[] needles) =>
        (line, _) => ContainsAny(line, needles);

    /// <summary>Like <see cref="Any"/>, and adds the <c>path</c> arg when the OS error names one.</summary>
    private static LineMatcher WithPath(params string[] needles) =>
        (line, args) =>
        {
            if (!ContainsAny(line, needles))
            {
                return false;
            }

            if (OsErrorPathRegex().Match(line) is { Success: true } match)
            {
                args["path"] = PyUnescape(match.Groups["path"].Value);
            }

            return true;
        };

    /// <summary>Matches a regex and adds its named groups as args.</summary>
    private static LineMatcher Capture(Regex regex) =>
        (line, args) =>
        {
            var match = regex.Match(line);
            if (!match.Success)
            {
                return false;
            }

            foreach (var group in match.Groups.Values)
            {
                if (group.Success && !char.IsAsciiDigit(group.Name[0]))
                {
                    args[group.Name] = group.Value;
                }
            }

            return true;
        };

    private static LineMatcher HttpStatus(Func<int, bool> accepts, bool withStatus = false) =>
        (line, args) =>
        {
            foreach (Match match in HttpErrorRegex().Matches(line))
            {
                var status = int.Parse(match.Groups["status"].ValueSpan, CultureInfo.InvariantCulture);
                if (accepts(status))
                {
                    if (withStatus)
                    {
                        args["status"] = match.Groups["status"].Value;
                    }

                    return true;
                }
            }

            return false;
        };

    private static bool ContainsAny(string line, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string InPath(IReadOnlyDictionary<string, string> args) =>
        args.TryGetValue("path", out var path) ? $" ({path})" : "";

    /// <summary>Undoes Python's <c>repr()</c> escaping of a path: <c>\\</c>, <c>\'</c> and <c>\"</c>.</summary>
    private static string PyUnescape(string repr) => PyEscapeRegex().Replace(repr, m => m.Groups["c"].Value);

    [GeneratedRegex(@"HTTP Error (?<status>\d{3})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HttpErrorRegex();

    [GeneratedRegex(@"Unsupported URL: (?<url>\S+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UnsupportedUrlRegex();

    /// <summary>argparse's usage error: <c>yt-dlp.exe: error: no such option: --foo</c>.</summary>
    [GeneratedRegex(@"^yt-dlp(\.exe)?: error: ", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UsageErrorRegex();

    [GeneratedRegex(@"fragment \d+ not found|unable to download fragment", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FragmentRegex();

    /// <summary>The quoted path after Python's <c>[Errno N] text: </c> / <c>[WinError N] text: </c>.</summary>
    [GeneratedRegex(@"\[(?:Errno|WinError) \d+\] [^:]*: (?<q>['""])(?<path>(?:\\.|(?!\k<q>).)*)\k<q>", RegexOptions.CultureInvariant)]
    private static partial Regex OsErrorPathRegex();

    [GeneratedRegex(@"\\(?<c>[\\'""])", RegexOptions.CultureInvariant)]
    private static partial Regex PyEscapeRegex();
}
