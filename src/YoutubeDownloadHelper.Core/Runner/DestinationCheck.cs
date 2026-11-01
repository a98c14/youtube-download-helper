namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>
/// The engine's check before a download starts (PLAN.md §5.4): when the destination already has a file with the
/// predicted name, yt-dlp would skip the download and report success, so the run must use the
/// <c> [&lt;videoId&gt;]</c> name instead (<see cref="DownloadRequest.AppendVideoId"/>).
/// </summary>
public static class DestinationCheck
{
    /// <summary>
    /// Whether a file named like <paramref name="predictedPath"/> exists, with any extension: the predicted extension
    /// is the downloaded one, and a conversion (MP3), a remux or the thumbnail embed (MKV) can change it.
    /// </summary>
    /// <param name="predictedPath">yt-dlp's predicted path (<see cref="ResolvedVideo.Filename"/>).</param>
    public static bool NameIsTaken(string predictedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(predictedPath);

        var folder = Path.GetDirectoryName(predictedPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(predictedPath);
        try
        {
            // Compared by hand: a title may contain characters that mean something in a search pattern.
            return Directory.EnumerateFiles(folder)
                .Any(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
