using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Tools;

namespace YoutubeDownloadHelper.Contracts.Events;

/// <summary>
/// The SSE <c>event:</c> names of <see cref="ApiRoutes.Events"/> (PLAN.md §6.3). Each event's <c>data:</c> is one JSON
/// object, the record named on the constant; its <c>id:</c> is the engine's event sequence number.
/// </summary>
public static class EventTypes
{
    /// <summary><see cref="ItemAddedData"/>.</summary>
    public const string ItemAdded = "item.added";

    /// <summary><see cref="ItemUpdatedData"/>: state or metadata changed. Completed and skipped items moved to history.</summary>
    public const string ItemUpdated = "item.updated";

    /// <summary><see cref="ItemProgressData"/>, at most 4 per second per item.</summary>
    public const string ItemProgress = "item.progress";

    /// <summary><see cref="ItemRemovedData"/>: the item is in neither the queue nor history any more.</summary>
    public const string ItemRemoved = "item.removed";

    /// <summary><see cref="QueueReorderedData"/>.</summary>
    public const string QueueReordered = "queue.reordered";

    /// <summary><see cref="QueueBlockData"/>: a queue block was raised.</summary>
    public const string QueueBlocked = "queue.blocked";

    /// <summary><see cref="QueueBlockData"/>: a queue block was lifted.</summary>
    public const string QueueUnblocked = "queue.unblocked";

    /// <summary><see cref="ToolsChangedData"/>: a tool was installed, or the tools' health changed.</summary>
    public const string ToolsChanged = "tools.changed";

    /// <summary>
    /// <see cref="ToolInstallData"/>: progress of a tool install, or its failure (the first-run wizard shows it). A
    /// successful install ends with <see cref="ToolsChanged"/>.
    /// </summary>
    public const string ToolInstall = "tools.install";

    /// <summary><see cref="NotifyData"/>: something to tell the user (a toast).</summary>
    public const string Notify = "notify";

    /// <summary><see cref="EngineStoppingData"/>: the engine is stopping; the stream ends after it.</summary>
    public const string EngineStopping = "engine.stopping";

    /// <summary>
    /// <see cref="ResyncData"/>: missed events cannot be replayed (or none were asked for). Refetch the snapshots
    /// (<see cref="ApiRoutes.Items"/>, <see cref="ApiRoutes.Info"/>) and ignore events whose id is not above the
    /// snapshot's <see cref="ItemList.Sequence"/>.
    /// </summary>
    public const string Resync = "resync";
}

public sealed record ItemAddedData(ItemInfo Item);

/// <param name="Item">The item as it is now.</param>
/// <param name="FromState">The state before, when the state changed.</param>
/// <param name="Reason">Why the state changed (see <see cref="ItemEventInfo.Reason"/>).</param>
public sealed record ItemUpdatedData(ItemInfo Item, ItemState? FromState, string? Reason);

public sealed record ItemProgressData(long ItemId, ItemProgressInfo Progress);

public sealed record ItemRemovedData(long ItemId);

public sealed record ItemPositionInfo(long ItemId, long Position);

/// <param name="Positions">The new positions of the items that moved.</param>
public sealed record QueueReorderedData(IReadOnlyList<ItemPositionInfo> Positions);

/// <param name="Block">The block.</param>
/// <param name="Reason">Why it was raised (<c>queue.block.*</c>); null when lifted, and for <see cref="QueueBlock.PausedByUser"/>.</param>
public sealed record QueueBlockData(QueueBlock Block, EngineMessage? Reason);

/// <param name="Tools">Every managed tool as it is now.</param>
/// <param name="Ready">Every tool is installed and works; downloads can run.</param>
public sealed record ToolsChangedData(IReadOnlyList<ToolInfo> Tools, bool Ready);

/// <summary>Where a tool install is. Success is a <see cref="EventTypes.ToolsChanged"/> event instead.</summary>
public enum ToolInstallStatus
{
    Downloading,
    Verifying,
    Extracting,
    Testing,
    Switching,

    /// <summary>Failed; the current version (if any) is unchanged. <see cref="ToolInstallData.Error"/> says why.</summary>
    Failed,
}

/// <param name="Tool">One of <see cref="ToolNames"/>.</param>
/// <param name="Version">The version being installed; null when a failure came before it was known.</param>
/// <param name="Status">Where the install is.</param>
/// <param name="BytesReceived">While downloading: bytes so far.</param>
/// <param name="TotalBytes">While downloading: the size, when known.</param>
/// <param name="Error">When failed: why (<see cref="ToolCodes"/>).</param>
public sealed record ToolInstallData(
    string Tool,
    string? Version,
    ToolInstallStatus Status,
    long? BytesReceived,
    long? TotalBytes,
    EngineMessage? Error);

/// <param name="Message">What to tell the user (<see cref="NotifyCodes"/>).</param>
/// <param name="ItemId">The item it is about, if any.</param>
public sealed record NotifyData(EngineMessage Message, long? ItemId);

public sealed record EngineStoppingData;

/// <param name="Sequence">The id of the last event before this stream's next one.</param>
public sealed record ResyncData(long Sequence);

/// <summary>Codes of <see cref="NotifyData.Message"/>.</summary>
public static class NotifyCodes
{
    /// <summary>A download finished. Args: <c>itemId</c>, <c>title</c> (the URL when the title is unknown), <c>filePath</c>.</summary>
    public const string ItemCompleted = "notify.item.completed";

    /// <summary>
    /// A download failed for good. Args: <c>itemId</c>, <c>title</c> (the URL when the title is unknown), <c>errorCode</c>
    /// (the item's <c>error.*</c> code, when it has one).
    /// </summary>
    public const string ItemFailed = "notify.item.failed";
}
