namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>
/// One yt-dlp invocation: the executable and its arguments, one <c>ProcessStartInfo.ArgumentList</c> entry each
/// (never joined into a command line, so nothing needs quoting).
/// </summary>
public sealed record YtDlpCommand(string FileName, IReadOnlyList<string> Arguments);
