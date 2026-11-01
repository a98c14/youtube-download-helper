namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>
/// The template strings passed to yt-dlp. The <c>YDH1</c> lines are from spike #33
/// (<c>docs/spikes/progress-and-paths.md</c>); the progress parser (#41) reads them, so change both together and
/// bump the <c>YDH1</c> version on an incompatible change.
/// </summary>
public static class YtDlpTemplates
{
    /// <summary>The prefix of every machine-readable line, trailing space included.</summary>
    public const string LinePrefix = "YDH1 ";

    public const string DownloadProgress =
        "download:YDH1 {\"k\":\"dl\",\"p\":%(progress.{status,downloaded_bytes,total_bytes,total_bytes_estimate,speed,eta,elapsed,fragment_index,fragment_count})j,\"i\":%(info.{id,format_id})j}";

    public const string PostProcessProgress =
        "postprocess:YDH1 {\"k\":\"pp\",\"p\":%(progress.{status,postprocessor})j,\"i\":%(info.{id,filepath})j}";

    public const string PlanPrint =
        "before_dl:YDH1 {\"k\":\"plan\",\"i\":%(.{id,title,channel,format_id,ext,filesize,filesize_approx})j,\"rf\":%(requested_formats.:.{format_id,ext,protocol,filesize,filesize_approx})j}";

    /// <summary>The final path, as ASCII-safe JSON (replaces a plain <c>--print after_move:filepath</c>).</summary>
    public const string MovedPrint = "after_move:YDH1 {\"k\":\"moved\",\"i\":%(.{id,filepath})j}";

    /// <summary><c>&lt;Channel&gt;\&lt;YYYY-MM-DD&gt; - &lt;Title&gt;.&lt;ext&gt;</c> (PLAN.md §3, §5.4).</summary>
    public const string Output = @"%(channel)s\%(upload_date>%Y-%m-%d)s - %(title)s.%(ext)s";

    /// <summary><see cref="Output"/> with <c> [&lt;videoId&gt;]</c>, for a name that already exists.</summary>
    public const string OutputWithVideoId = @"%(channel)s\%(upload_date>%Y-%m-%d)s - %(title)s [%(id)s].%(ext)s";

    /// <summary>The feed the auth verification fetches (PLAN.md §5.7); it only lists entries when logged in.</summary>
    public const string SubscriptionsFeedUrl = "https://www.youtube.com/feed/subscriptions";
}
