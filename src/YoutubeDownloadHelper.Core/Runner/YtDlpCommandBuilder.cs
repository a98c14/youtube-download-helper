using System.Globalization;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Presets;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>
/// Builds every yt-dlp command line the engine runs (PLAN.md §5.4): downloads, resolves, flat listings and the
/// auth verification. Each command takes one <see cref="IToolLocator"/> snapshot. The output never depends on the
/// current culture.
/// </summary>
public sealed class YtDlpCommandBuilder(DataPaths paths, IToolLocator tools, PresetRegistry presets)
{
    /// <summary>The default number of entries a flat listing fetches (a watcher poll, PLAN.md §5.5).</summary>
    public const int DefaultListingEnd = 30;

    /// <summary>A single-item download with progress lines, embeds, work and destination folders.</summary>
    public YtDlpCommand Download(DownloadRequest request)
    {
        var output = Output(request);
        var snapshot = tools.Current;
        List<string> args =
        [
            .. Common(snapshot, request.CookiesFile),
            "--no-playlist",
            "--newline",
            "--progress",
            "--progress-delta", "0.25",
            "--progress-template", YtDlpTemplates.DownloadProgress,
            "--progress-template", YtDlpTemplates.PostProcessProgress,
            "--print", YtDlpTemplates.PlanPrint,
            "--print", YtDlpTemplates.MovedPrint,
            "--embed-metadata",
            "--embed-thumbnail",
            "--embed-chapters",
            .. output,
            .. Url(request.Url),
        ];
        return new YtDlpCommand(snapshot.YtDlp, args);
    }

    /// <summary>
    /// The resolve before a download: <see cref="Resolve(string, string?)"/> with the download's folders, output
    /// template and format arguments, so the JSON's <c>filename</c> is the path the download will produce (up to the
    /// extension; <see cref="ResolvedVideo.Filename"/>) and its sizes are those of the formats it will download.
    /// </summary>
    public YtDlpCommand Resolve(DownloadRequest request)
    {
        var output = Output(request);
        var snapshot = tools.Current;
        return new YtDlpCommand(
            snapshot.YtDlp,
            [.. Common(snapshot, request.CookiesFile), "-J", "--no-playlist", .. output, .. Url(request.Url)]);
    }

    /// <summary>Metadata of a single video as one JSON object (<c>-J</c>), for the Add dialog and before a download.</summary>
    public YtDlpCommand Resolve(string url, string? cookiesFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var snapshot = tools.Current;
        return new YtDlpCommand(snapshot.YtDlp, [.. Common(snapshot, cookiesFile), "-J", "--no-playlist", .. Url(url)]);
    }

    /// <summary>
    /// The first <paramref name="playlistEnd"/> entries of a playlist or channel tab, without resolving each one
    /// (<c>--flat-playlist -J</c>): the playlist picker and watcher polls.
    /// </summary>
    public YtDlpCommand FlatListing(string url, int playlistEnd = DefaultListingEnd, string? cookiesFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfLessThan(playlistEnd, 1);

        var snapshot = tools.Current;
        return new YtDlpCommand(snapshot.YtDlp, [.. Common(snapshot, cookiesFile), .. Listing(playlistEnd), .. Url(url)]);
    }

    /// <summary>
    /// The auth verification and keep-alive ping (PLAN.md §5.7): a flat fetch of the subscriptions feed, which
    /// succeeds only when the cookies are logged in. One entry is enough.
    /// </summary>
    public YtDlpCommand VerifyAuth(string cookiesFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookiesFile);

        var snapshot = tools.Current;
        return new YtDlpCommand(
            snapshot.YtDlp,
            [.. Common(snapshot, cookiesFile), .. Listing(1), .. Url(YtDlpTemplates.SubscriptionsFeedUrl)]);
    }

    /// <summary>
    /// On every run: no user config, UTF-8 output (spike #33), a private cache, the managed ffmpeg and Deno, and the
    /// cookies file if there is one.
    /// </summary>
    private List<string> Common(ToolPaths snapshot, string? cookiesFile)
    {
        List<string> args =
        [
            "--ignore-config",
            "--encoding", "utf-8",
            "--cache-dir", paths.YtDlpCacheDir,
            "--ffmpeg-location", snapshot.Ffmpeg,
            "--js-runtimes", "deno:" + snapshot.Deno,
        ];
        if (!string.IsNullOrWhiteSpace(cookiesFile))
        {
            args.AddRange(["--cookies", cookiesFile]);
        }

        return args;
    }

    /// <summary>The work and destination folders, the output template and the preset's format arguments.</summary>
    private List<string> Output(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Url);
        if (!Path.IsPathFullyQualified(request.DownloadFolder))
        {
            throw new ArgumentException("The download folder must be an absolute path.", nameof(request));
        }

        var preset = presets.Get(request.PresetId);
        var destination = string.IsNullOrWhiteSpace(request.FolderOverride)
            ? request.DownloadFolder
            : Path.Combine(request.DownloadFolder, request.FolderOverride);
        return
        [
            "-P", "temp:" + paths.WorkDirFor(request.ItemId),
            "-P", "home:" + Path.GetFullPath(destination),
            "-o", request.AppendVideoId ? YtDlpTemplates.OutputWithVideoId : YtDlpTemplates.Output,
            .. preset.FormatArguments,
        ];
    }

    private static string[] Listing(int playlistEnd) =>
        ["--flat-playlist", "-J", "--playlist-end", playlistEnd.ToString(CultureInfo.InvariantCulture)];

    /// <summary><c>--</c> ends the options, so a URL starting with <c>-</c> is never read as one.</summary>
    private static string[] Url(string url) => ["--", url];
}
