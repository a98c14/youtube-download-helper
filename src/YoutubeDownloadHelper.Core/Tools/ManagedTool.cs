using YoutubeDownloadHelper.Contracts.Tools;

namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>A tool the tool manager installs (PLAN.md §5.6): its binaries and how to smoke-test them.</summary>
/// <param name="Name">One of <see cref="ToolNames"/>; also its folder under <c>tools\</c> and its <c>tools</c> row.</param>
/// <param name="Executables">The binaries a version folder must hold; the first is the one yt-dlp is pointed at.</param>
/// <param name="SmokeArguments">Prints the version and exits 0 (<c>--version</c>, or <c>-version</c> for ffmpeg).</param>
public sealed record ManagedTool(string Name, IReadOnlyList<string> Executables, IReadOnlyList<string> SmokeArguments)
{
    public static ManagedTool YtDlp { get; } = new(ToolNames.YtDlp, ["yt-dlp.exe"], ["--version"]);

    /// <summary>yt-dlp's FFmpeg build: <c>ffmpeg.exe</c> and <c>ffprobe.exe</c>.</summary>
    public static ManagedTool Ffmpeg { get; } = new(ToolNames.Ffmpeg, ["ffmpeg.exe", "ffprobe.exe"], ["-version"]);

    /// <summary>yt-dlp's JS runtime for YouTube's challenges.</summary>
    public static ManagedTool Deno { get; } = new(ToolNames.Deno, ["deno.exe"], ["--version"]);

    /// <summary>In <see cref="ToolNames"/> order, as <c>GET /info</c> lists them.</summary>
    public static IReadOnlyList<ManagedTool> All { get; } = [YtDlp, Ffmpeg, Deno];

    public string MainExecutable => Executables[0];
}
