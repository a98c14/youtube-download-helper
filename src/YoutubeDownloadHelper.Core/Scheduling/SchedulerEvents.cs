using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Events;

namespace YoutubeDownloadHelper.Core.Scheduling;

/// <summary>
/// A queue block other than <see cref="QueueBlock.PausedByUser"/> (which is <c>QueuePausedChangedEvent</c>) was raised
/// or lifted (PLAN.md §4.3). <paramref name="Reason"/> says why a block was raised; it is null when one is lifted.
/// </summary>
public sealed record QueueBlockChangedEvent(QueueBlock Block, bool Active, EngineMessage? Reason) : EngineEvent;
