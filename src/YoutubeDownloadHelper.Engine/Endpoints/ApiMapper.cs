using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Settings;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;

namespace YoutubeDownloadHelper.Engine.Endpoints;

/// <summary>Core types to the Contracts DTOs the API and the event stream return.</summary>
public static class ApiMapper
{
    public static ItemInfo ToInfo(QueueItem item, ItemProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var inQueue = item.State is not (ItemState.Completed or ItemState.Skipped or ItemState.Removed or ItemState.Seen);
        return new ItemInfo(
            item.Id,
            item.Url,
            item.Extractor,
            item.VideoId,
            item.Title,
            item.Channel,
            item.ChannelId,
            item.UploadDate,
            item.DurationSeconds,
            item.Availability,
            item.LiveStatus,
            item.PresetId,
            item.Folder,
            item.Force,
            item.State,
            inQueue ? item.Position : 0,
            item.Attempt,
            item.NextAttemptAt,
            item.ErrorCategory,
            item.Error,
            item.FilePath,
            item.SizeBytes,
            item.WatcherId,
            item.CreatedAt,
            item.UpdatedAt,
            progress is not null && ItemStateMachine.IsRunning(item.State) ? ToInfo(progress) : null);
    }

    public static ItemProgressInfo ToInfo(ItemProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return new ItemProgressInfo(
            progress.Phase,
            progress.DownloadedBytes,
            progress.TotalBytes,
            progress.SpeedBytesPerSecond,
            progress.EtaSeconds,
            progress.Fraction);
    }

    public static ItemEventInfo ToInfo(ItemEventEntity itemEvent)
    {
        ArgumentNullException.ThrowIfNull(itemEvent);
        return new ItemEventInfo(
            itemEvent.CreatedAt,
            itemEvent.FromState,
            itemEvent.ToState,
            itemEvent.Reason,
            ParseCategory(itemEvent.ErrorCategory),
            itemEvent.ExitCode);
    }

    /// <summary>
    /// The yt-dlp runs in an item's transitions: a run starts with the event into <see cref="ItemState.Resolving"/> and
    /// ends with the first later event into a state that is not running; that event has the run's exit code and output.
    /// </summary>
    public static IReadOnlyList<AttemptInfo> ToAttempts(IEnumerable<ItemEventEntity> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var attempts = new List<AttemptInfo>();
        ItemEventEntity? start = null;
        foreach (var e in events)
        {
            if (e.ToState == ItemState.Resolving)
            {
                if (start is not null)
                {
                    attempts.Add(new AttemptInfo(attempts.Count + 1, start.CreatedAt, null, null, null, null, []));
                }

                start = e;
            }
            else if (start is not null && !ItemStateMachine.IsRunning(e.ToState))
            {
                attempts.Add(new AttemptInfo(attempts.Count + 1, start.CreatedAt, e.CreatedAt, e.ToState, e.Reason, e.ExitCode, SplitLines(e.OutputTail)));
                start = null;
            }
        }

        if (start is not null)
        {
            attempts.Add(new AttemptInfo(attempts.Count + 1, start.CreatedAt, null, null, null, null, []));
        }

        return attempts;
    }

    public static SettingsInfo ToInfo(EngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SettingsInfo(
            settings.DownloadFolder,
            settings.DefaultPresetId,
            settings.Concurrency,
            settings.ApiPort,
            settings.YtDlpChannel,
            settings.KeepDownloadingAfterExit,
            settings.UiLanguage,
            settings.CloseToTray);
    }

    private static IReadOnlyList<string> SplitLines(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Split('\n');

    private static ErrorCategory? ParseCategory(string? name) =>
        Enum.TryParse<ErrorCategory>(name, ignoreCase: false, out var category) ? category : null;
}
