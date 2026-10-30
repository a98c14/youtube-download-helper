namespace YoutubeDownloadHelper.FakeYtDlp;

/// <summary>
/// yt-dlp's error text, kept verbatim (including the double spaces around URLs and the curly apostrophe in the
/// bot check) so the error classifier's fixtures match real output. See the README for where each comes from.
/// </summary>
internal static class YtDlpMessages
{
    public const string CookiesHint =
        "Use --cookies-from-browser or --cookies for the authentication. See  " +
        "https://github.com/yt-dlp/yt-dlp/wiki/FAQ#how-do-i-pass-cookies-to-yt-dlp  for how to manually pass cookies. " +
        "Also see  https://github.com/yt-dlp/yt-dlp/wiki/Extractors#exporting-youtube-cookies  for tips on " +
        "effectively exporting YouTube cookies";

    public const string ReportBug =
        "please report this issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate " +
        "issue template. Confirm you are on the latest version using  yt-dlp -U";

    public const string ConnectionReset =
        "('Connection aborted.', ConnectionResetError(10054, 'An existing connection was forcibly closed by the " +
        "remote host', None, 10054, None))";

    /// <summary>Errors raised while extracting the video, before any format is chosen.</summary>
    public static IReadOnlyList<string>? Extraction(string scenario, string id, bool loggedIn) => scenario switch
    {
        "members-only" when !loggedIn =>
            [$"ERROR: [youtube] {id}: Join this channel to get access to members-only content like this video, and other exclusive perks."],
        "premium-only" when !loggedIn =>
            [$"ERROR: [youtube] {id}: This video is only available to Music Premium members"],
        "age-restricted" when !loggedIn =>
            [$"ERROR: [youtube] {id}: Sign in to confirm your age. This video may be inappropriate for some users. {CookiesHint}"],
        "bot-check" =>
            [$"ERROR: [youtube] {id}: Sign in to confirm you’re not a bot. {CookiesHint}"],
        "private" =>
            [$"ERROR: [youtube] {id}: Private video. Sign in if you've been granted access to this video. {CookiesHint}"],
        "removed" =>
            [$"ERROR: [youtube] {id}: Video unavailable. This video has been removed by the uploader"],
        "geo-blocked" =>
            [$"ERROR: [youtube] {id}: The uploader has not made this video available in your country. You might want to use a VPN or a proxy server (with --proxy) to workaround."],
        "extractor-failure" =>
            [$"ERROR: [youtube] {id}: Unable to extract initial player response; {ReportBug}"],
        "nsig" =>
            [
                $"WARNING: [youtube] {id}: nsig extraction failed: Some formats may be missing",
                "         n = fAkEnSiGvAlUe ; player = https://www.youtube.com/s/player/0fake0nsig/player_ias.vflset/en_US/base.js",
                $"ERROR: [youtube] {id}: Requested format is not available. Use --list-formats for a list of available formats",
            ],
        "unknown-error" =>
            [$"ERROR: [youtube] {id}: Something went wrong in the fake (unknown error)"],
        _ => null,
    };

    public static string HttpError(int status) =>
        $"ERROR: unable to download video data: HTTP Error {status}: {Reason(status)}";

    public static string Upcoming(string id) => $"ERROR: [youtube] {id}: This live event will begin in a few moments.";

    public static string AccessDenied(string path) =>
        $"ERROR: unable to open for writing: [Errno 13] Permission denied: {PyRepr(path)}";

    public const string DiskFull = "ERROR: unable to write data: [Errno 28] No space left on device";

    public const string LoginRequired = $"ERROR: [youtube:tab] Login details are needed to download this content. {CookiesHint}";

    public static string PlaylistDoesNotExist(string id) => $"ERROR: [youtube:tab] {id}: The playlist does not exist.";

    public static string Unsupported(string url) => $"ERROR: Unsupported URL: {url}";

    /// <summary>Python's <c>repr()</c> of a string, as it appears in <c>OSError</c> messages.</summary>
    public static string PyRepr(string s) => "'" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static string Reason(int status) => status switch
    {
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Error",
    };
}
