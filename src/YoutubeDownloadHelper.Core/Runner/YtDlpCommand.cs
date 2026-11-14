using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>
/// One yt-dlp invocation: the executable and its arguments, one <c>ProcessStartInfo.ArgumentList</c> entry each
/// (never joined into a command line, so nothing needs quoting).
/// </summary>
/// <param name="FileName">The yt-dlp executable.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Tools">
/// The tool snapshot the command was built from; <see cref="YtDlpRunner"/> marks it in use (<see cref="IToolUsage"/>)
/// while the process runs.
/// </param>
public sealed record YtDlpCommand(string FileName, IReadOnlyList<string> Arguments, ToolPaths? Tools = null);
