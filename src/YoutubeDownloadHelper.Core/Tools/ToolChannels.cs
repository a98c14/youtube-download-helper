using YoutubeDownloadHelper.Contracts.Settings;

namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Release channels as the feed (<see cref="ToolRelease.Channel"/>) and the <c>tools</c> table store them (PLAN.md §5.6).
/// Only yt-dlp has channels; the other tools' channel is <see langword="null"/>.
/// </summary>
public static class ToolChannels
{
    /// <summary><c>yt-dlp/yt-dlp</c> releases.</summary>
    public const string Stable = "stable";

    /// <summary><c>yt-dlp/yt-dlp-nightly-builds</c> releases.</summary>
    public const string Nightly = "nightly";

    public static string Name(YtDlpChannel channel) => channel switch
    {
        YtDlpChannel.Stable => Stable,
        YtDlpChannel.Nightly => Nightly,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };

    /// <summary>The setting's value for a stored channel name; <see langword="null"/> for none or an unknown one.</summary>
    public static YtDlpChannel? Parse(string? name) => name switch
    {
        Stable => YtDlpChannel.Stable,
        Nightly => YtDlpChannel.Nightly,
        _ => null,
    };
}
