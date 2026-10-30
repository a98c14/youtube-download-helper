using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>One state transition of an item (table <c>item_events</c>, PLAN.md §5.1).</summary>
public sealed class ItemEventEntity
{
    public long Id { get; set; }

    public Guid ItemId { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Null for the event that created the item.</summary>
    public ItemState? FromState { get; set; }

    public ItemState ToState { get; set; }

    public string? Reason { get; set; }

    public string? ErrorCategory { get; set; }

    public int Attempt { get; set; }
}
