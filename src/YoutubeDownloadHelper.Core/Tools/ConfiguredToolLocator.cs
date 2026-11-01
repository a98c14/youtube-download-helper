using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Fixed tool paths: the <see cref="ToolLocatorOptions"/> overrides, else <c>tools\yt-dlp\yt-dlp.exe</c>,
/// <c>tools\ffmpeg\ffmpeg.exe</c> and <c>tools\deno\deno.exe</c> under the data folder. It does not check that the
/// files exist; the tool manager (#43) owns installs, versions and health checks.
/// </summary>
public sealed class ConfiguredToolLocator(DataPaths paths, ToolLocatorOptions options) : IToolLocator
{
    public ToolPaths Current { get; } = new(
        Resolve(options.YtDlp, Path.Combine(paths.ToolsDir, "yt-dlp", "yt-dlp.exe")),
        Resolve(options.Ffmpeg, Path.Combine(paths.ToolsDir, "ffmpeg", "ffmpeg.exe")),
        Resolve(options.Deno, Path.Combine(paths.ToolsDir, "deno", "deno.exe")));

    private static string Resolve(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : Path.GetFullPath(configured);
}
