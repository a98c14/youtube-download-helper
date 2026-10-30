using YoutubeDownloadHelper.Contracts;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>yt-dlp release channel (PLAN.md §5.6).</summary>
public enum YtDlpChannel
{
    Stable,
    Nightly,
}

/// <summary>
/// Every setting stored in the <c>settings</c> table, with its default. Load and save through
/// <see cref="SettingsStore"/>; a setting the user never changed is not stored, so it follows the default.
/// </summary>
public sealed record EngineSettings
{
    public const int DefaultApiPort = 47815;

    public required string DownloadFolder { get; init; }

    public string DefaultPresetId { get; init; } = "best";

    /// <summary>Fixed at 2 in v1; only changeable by editing the database (PLAN.md §3).</summary>
    public int Concurrency { get; init; } = 2;

    public int ApiPort { get; init; } = DefaultApiPort;

    public YtDlpChannel YtDlpChannel { get; init; } = YtDlpChannel.Stable;

    public bool KeepDownloadingAfterExit { get; init; }

    /// <summary>A culture name (<c>tr-TR</c> or <c>en-US</c>), used by the app only.</summary>
    public string UiLanguage { get; init; } = "tr-TR";

    public bool CloseToTray { get; init; } = true;

    /// <summary>The defaults, with the download folder at <c>%USERPROFILE%\Downloads\Youtube İndirgeç</c>.</summary>
    public static EngineSettings Defaults(string userProfile) =>
        new() { DownloadFolder = Path.Combine(userProfile, "Downloads", AppIdentity.DownloadFolderName) };

    public static EngineSettings DefaultsForCurrentUser() =>
        Defaults(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify));
}
