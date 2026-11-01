namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>What <see cref="YtDlpCommandBuilder.Download"/> needs to build one download run.</summary>
public sealed record DownloadRequest
{
    /// <summary>The queue item; its work folder <c>data\work\&lt;itemId&gt;\</c> holds the partial files.</summary>
    public required long ItemId { get; init; }

    public required string Url { get; init; }

    public required string PresetId { get; init; }

    /// <summary>The download folder setting. Must be an absolute path.</summary>
    public required string DownloadFolder { get; init; }

    /// <summary>
    /// The item's folder (from the Add dialog or its watcher), used instead of <see cref="DownloadFolder"/>; the
    /// <c>&lt;Channel&gt;</c> subfolder is still created inside it. A relative path is taken as relative to
    /// <see cref="DownloadFolder"/>.
    /// </summary>
    public string? FolderOverride { get; init; }

    /// <summary>
    /// Appends <c> [&lt;videoId&gt;]</c> to the file name. The runner sets it when the plain name already exists in
    /// the destination (PLAN.md §5.4).
    /// </summary>
    public bool AppendVideoId { get; init; }

    /// <summary>The job's Netscape cookies file, or <see langword="null"/> to run without cookies.</summary>
    public string? CookiesFile { get; init; }
}
