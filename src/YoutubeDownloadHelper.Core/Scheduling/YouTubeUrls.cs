namespace YoutubeDownloadHelper.Core.Scheduling;

/// <summary>Which items count as YouTube for the delay between job starts (PLAN.md §4.3).</summary>
public static class YouTubeUrls
{
    /// <summary>The extractor says YouTube, or (before resolving) the URL is on a YouTube host.</summary>
    public static bool IsYouTube(string url, string? extractor)
    {
        if (extractor is not null)
        {
            return extractor.StartsWith("youtube", StringComparison.OrdinalIgnoreCase);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            || IsHostOrSubdomain(host, "youtube.com")
            || IsHostOrSubdomain(host, "youtube-nocookie.com");
    }

    /// <summary>yt-dlp's extractor key for YouTube videos (<c>ItemEntity.Extractor</c>).</summary>
    public const string Extractor = "youtube";

    /// <summary>A YouTube video ID: 11 characters of <c>A-Z a-z 0-9 _ -</c>.</summary>
    public static bool IsVideoId(string? id) =>
        id is { Length: 11 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>The watch URL of a video ID.</summary>
    public static string WatchUrl(string videoId) => "https://www.youtube.com/watch?v=" + videoId;

    /// <summary>
    /// The video ID of a single-video YouTube URL (<c>watch?v=</c>, <c>youtu.be/</c>, <c>/shorts/</c>, <c>/live/</c>,
    /// <c>/embed/</c>, <c>/v/</c>), without asking yt-dlp. Null for anything else (channels, playlists, other sites).
    /// </summary>
    public static string? TryGetVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var host = uri.Host;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = null;
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            id = segments.Length >= 1 ? segments[0] : null;
        }
        else if (IsHostOrSubdomain(host, "youtube.com") || IsHostOrSubdomain(host, "youtube-nocookie.com"))
        {
            if (segments is ["watch"])
            {
                id = QueryValue(uri.Query, "v");
            }
            else if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed" or "v")
            {
                id = segments[1];
            }
        }

        return IsVideoId(id) ? id : null;
    }

    private static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && pair.AsSpan(0, equals).Equals(name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return null;
    }

    private static bool IsHostOrSubdomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
