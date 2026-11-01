using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Events;

namespace YoutubeDownloadHelper.Core.Queue;

/// <summary>A new item (<c>item.added</c>).</summary>
public sealed record ItemAddedEvent(QueueItem Item) : EngineEvent;

/// <summary>
/// An item changed (<c>item.updated</c>). <paramref name="FromState"/> and <paramref name="Reason"/> are set when its
/// state changed. Items that entered <see cref="ItemState.Completed"/> or <see cref="ItemState.Skipped"/> have left the
/// queue for history.
/// </summary>
public sealed record ItemUpdatedEvent(QueueItem Item, ItemState? FromState, string? Reason) : EngineEvent;

/// <summary>Progress of a running item (<c>item.progress</c>); not persisted, not throttled here.</summary>
public sealed record ItemProgressEvent(long ItemId, ItemProgress Progress) : EngineEvent;

/// <summary>The user removed an item (<c>item.removed</c>); it is in neither the queue nor history.</summary>
public sealed record ItemRemovedEvent(long ItemId) : EngineEvent;

public sealed record ItemPosition(long ItemId, long Position);

/// <summary>Queue positions changed (<c>queue.reordered</c>); lists only the items that moved.</summary>
public sealed record QueueReorderedEvent(IReadOnlyList<ItemPosition> Positions) : EngineEvent;

/// <summary>The user paused or resumed the whole queue (the <c>PausedByUser</c> queue block, PLAN.md §4.3).</summary>
public sealed record QueuePausedChangedEvent(bool Paused) : EngineEvent;
