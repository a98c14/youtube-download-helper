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

    private static bool IsHostOrSubdomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
