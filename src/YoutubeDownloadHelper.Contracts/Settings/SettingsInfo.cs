namespace YoutubeDownloadHelper.Contracts.Settings;

/// <summary>The user's settings (<c>GET</c> <see cref="ApiRoutes.Settings"/>), stored in the engine's database.</summary>
/// <param name="DownloadFolder">Where downloads go unless an item has its own folder. An absolute path.</param>
/// <param name="DefaultPresetId">The preset of items added without one.</param>
/// <param name="Concurrency">How many downloads run at once. Read-only (fixed at 2 in v1).</param>
/// <param name="ApiPort">The port the engine listens on from its next start (the actual port is in the discovery file).</param>
/// <param name="YtDlpChannel">The yt-dlp release channel.</param>
/// <param name="KeepDownloadingAfterExit">The engine keeps running when the app exits, and does not exit when idle.</param>
/// <param name="UiLanguage">The app's language: <c>tr-TR</c> or <c>en-US</c>. Not used by the engine.</param>
/// <param name="CloseToTray">The app's close button minimizes to the tray. Not used by the engine.</param>
public sealed record SettingsInfo(
    string DownloadFolder,
    string DefaultPresetId,
    int Concurrency,
    int ApiPort,
    YtDlpChannel YtDlpChannel,
    bool KeepDownloadingAfterExit,
    string UiLanguage,
    bool CloseToTray);

/// <summary>
/// The body of <c>PATCH</c> <see cref="ApiRoutes.Settings"/>: only the settings that are present (not null) change.
/// Answers the new <see cref="SettingsInfo"/>, or <c>400</c> with <see cref="Api.ApiErrorCodes.InvalidParameter"/> and
/// nothing changed.
/// </summary>
public sealed record SettingsPatch(
    string? DownloadFolder = null,
    string? DefaultPresetId = null,
    int? ApiPort = null,
    YtDlpChannel? YtDlpChannel = null,
    bool? KeepDownloadingAfterExit = null,
    string? UiLanguage = null,
    bool? CloseToTray = null);

/// <summary>The values <see cref="SettingsInfo.UiLanguage"/> accepts.</summary>
public static class UiLanguages
{
    public const string Turkish = "tr-TR";

    public const string English = "en-US";

    public static IReadOnlyList<string> All { get; } = [Turkish, English];
}
