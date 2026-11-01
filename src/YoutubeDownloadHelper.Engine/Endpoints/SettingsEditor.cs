using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Settings;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Presets;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Endpoints;

/// <summary>
/// <c>PATCH /settings</c>: validates every given setting, then stores them together. Patches are serialized, so two
/// concurrent ones cannot undo each other.
/// </summary>
public sealed class SettingsEditor(SettingsStore store, PresetRegistry presets) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<(EngineSettings? Settings, EngineMessage? Invalid)> PatchAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (Validate(patch) is { } invalid)
        {
            return (null, invalid);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await store.LoadAsync(cancellationToken);
            var updated = current with
            {
                DownloadFolder = patch.DownloadFolder ?? current.DownloadFolder,
                DefaultPresetId = patch.DefaultPresetId ?? current.DefaultPresetId,
                ApiPort = patch.ApiPort ?? current.ApiPort,
                YtDlpChannel = patch.YtDlpChannel ?? current.YtDlpChannel,
                KeepDownloadingAfterExit = patch.KeepDownloadingAfterExit ?? current.KeepDownloadingAfterExit,
                UiLanguage = patch.UiLanguage ?? current.UiLanguage,
                CloseToTray = patch.CloseToTray ?? current.CloseToTray,
            };

            if (updated != current)
            {
                await store.SaveAsync(updated, CancellationToken.None);
            }

            return (updated, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private EngineMessage? Validate(SettingsPatch patch)
    {
        if (patch.DownloadFolder is { } folder
            && (string.IsNullOrWhiteSpace(folder) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(folder)))
        {
            return ApiErrors.InvalidParameter("downloadFolder", "The download folder must be an absolute path.");
        }

        if (patch.DefaultPresetId is { } presetId && !presets.Contains(presetId))
        {
            return ApiErrors.InvalidParameter("defaultPresetId", $"Unknown preset '{presetId}'.");
        }

        if (patch.ApiPort is < 1 or > 65535)
        {
            return ApiErrors.InvalidParameter("apiPort", "The port must be between 1 and 65535.");
        }

        if (patch.YtDlpChannel is { } channel && !Enum.IsDefined(channel))
        {
            return ApiErrors.InvalidParameter("ytDlpChannel", "Unknown yt-dlp channel.");
        }

        if (patch.UiLanguage is { } language && !UiLanguages.All.Contains(language, StringComparer.Ordinal))
        {
            return ApiErrors.InvalidParameter("uiLanguage", "The language must be tr-TR or en-US.");
        }

        return null;
    }
}
