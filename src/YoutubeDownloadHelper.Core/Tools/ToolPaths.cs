namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// The tool binaries one yt-dlp run uses (PLAN.md §5.6). Take one snapshot per run, so the executable and its
/// arguments always come from the same versions, even while the tool manager switches to new ones.
/// </summary>
/// <param name="YtDlp">Path to <c>yt-dlp.exe</c>.</param>
/// <param name="Ffmpeg">Path to <c>ffmpeg.exe</c> or its folder (ffprobe is found next to it).</param>
/// <param name="Deno">Path to <c>deno.exe</c>, yt-dlp's JS runtime.</param>
public sealed record ToolPaths(string YtDlp, string Ffmpeg, string Deno);
