namespace YoutubeDownloadHelper.Contracts.Presets;

/// <summary>
/// The quality presets (PLAN.md §5.3). Items and watchers store these IDs; the yt-dlp arguments behind them live in
/// the engine only. Compared ordinally.
/// </summary>
public static class PresetIds
{
    /// <summary>yt-dlp's default format selection and container. The default preset.</summary>
    public const string Best = "best";

    public const string P1080 = "1080p";

    public const string P720 = "720p";

    public const string AudioM4a = "audio-m4a";

    public const string AudioMp3 = "audio-mp3";
}
