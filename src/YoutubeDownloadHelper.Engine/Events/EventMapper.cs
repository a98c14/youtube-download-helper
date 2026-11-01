using System.Globalization;
using YoutubeDownloadHelper.Contracts.Events;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Events;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Scheduling;
using YoutubeDownloadHelper.Core.Tools;
using YoutubeDownloadHelper.Engine.Endpoints;

namespace YoutubeDownloadHelper.Engine.Events;

/// <summary>One SSE event before it is numbered: its <c>event:</c> name and its <c>data:</c> object.</summary>
public sealed record StreamEvent(string Type, object Data);

/// <summary>
/// Maps the internal bus (<see cref="EngineEvent"/>) to the API's events (<see cref="EventTypes"/>, PLAN.md §6.3).
/// Events the API does not show map to null. Later features (watchers, auth) add their cases here.
/// </summary>
public sealed class EventMapper(IToolStatusProvider tools, IToolHealth toolHealth)
{
    public StreamEvent? Map(EngineEvent engineEvent) => engineEvent switch
    {
        ItemAddedEvent e => new(EventTypes.ItemAdded, new ItemAddedData(ApiMapper.ToInfo(e.Item))),
        ItemUpdatedEvent e => new(EventTypes.ItemUpdated, new ItemUpdatedData(ApiMapper.ToInfo(e.Item), e.FromState, e.Reason)),
        ItemProgressEvent e => new(EventTypes.ItemProgress, new ItemProgressData(e.ItemId, ApiMapper.ToInfo(e.Progress))),
        ItemRemovedEvent e => new(EventTypes.ItemRemoved, new ItemRemovedData(e.ItemId)),
        QueueReorderedEvent e => new(
            EventTypes.QueueReordered,
            new QueueReorderedData([.. e.Positions.Select(p => new ItemPositionInfo(p.ItemId, p.Position))])),
        QueuePausedChangedEvent e => new(
            e.Paused ? EventTypes.QueueBlocked : EventTypes.QueueUnblocked,
            new QueueBlockData(QueueBlock.PausedByUser, null)),
        QueueBlockChangedEvent e => new(
            e.Active ? EventTypes.QueueBlocked : EventTypes.QueueUnblocked,
            new QueueBlockData(e.Block, e.Active ? e.Reason : null)),
        ToolInstallProgressEvent e => new(
            EventTypes.ToolInstall,
            new ToolInstallData(e.Tool, e.Version, ToStatus(e.Phase), e.BytesReceived, e.TotalBytes, null)),
        ToolInstallFailedEvent e => new(
            EventTypes.ToolInstall,
            new ToolInstallData(e.Tool, null, ToolInstallStatus.Failed, null, null, e.Reason)),
        ToolInstalledEvent => new(EventTypes.ToolsChanged, new ToolsChangedData(tools.GetTools(), toolHealth.ToolsReady)),
        ToolHealthChangedEvent e => new(EventTypes.ToolsChanged, new ToolsChangedData(tools.GetTools(), e.ToolsReady)),
        NotifyEvent e => new(EventTypes.Notify, new NotifyData(e.Message, e.ItemId)),
        EngineStoppingEvent => new(EventTypes.EngineStopping, new EngineStoppingData()),
        _ => null,
    };

    /// <summary>The notification an event causes (a download completed or failed for good), if any.</summary>
    public static NotifyEvent? NotificationFor(EngineEvent engineEvent)
    {
        if (engineEvent is not ItemUpdatedEvent { FromState: { } from } e || from == e.Item.State)
        {
            return null;
        }

        var item = e.Item;
        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["itemId"] = item.Id.ToString(CultureInfo.InvariantCulture),
            ["title"] = item.Title ?? item.Url,
        };

        switch (item.State)
        {
            case ItemState.Completed:
                args["filePath"] = item.FilePath ?? string.Empty;
                return new NotifyEvent(new EngineMessage(NotifyCodes.ItemCompleted, args, $"Downloaded: {args["title"]}"), item.Id);
            case ItemState.Failed:
                if (item.Error?.Code is { } code)
                {
                    args["errorCode"] = code;
                }

                return new NotifyEvent(
                    new EngineMessage(NotifyCodes.ItemFailed, args, item.Error is { } error ? $"Download failed: {args["title"]}: {error.Message}" : $"Download failed: {args["title"]}"),
                    item.Id);
            default:
                return null;
        }
    }

    private static ToolInstallStatus ToStatus(ToolInstallPhase phase) => phase switch
    {
        ToolInstallPhase.Downloading => ToolInstallStatus.Downloading,
        ToolInstallPhase.Verifying => ToolInstallStatus.Verifying,
        ToolInstallPhase.Extracting => ToolInstallStatus.Extracting,
        ToolInstallPhase.Testing => ToolInstallStatus.Testing,
        ToolInstallPhase.Switching => ToolInstallStatus.Switching,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };
}
