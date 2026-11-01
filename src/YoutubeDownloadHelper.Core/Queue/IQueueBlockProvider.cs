using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Core.Queue;

/// <summary>
/// The queue-level blocks (PLAN.md §4.3) as <c>GET /info</c> reports them. <see cref="PauseQueueBlockProvider"/> is
/// what <c>AddQueue()</c> registers; <c>AddScheduling()</c> replaces it with the <see cref="Scheduling.Scheduler"/>,
/// which also knows the network, disk and tool blocks.
/// </summary>
public interface IQueueBlockProvider
{
    IReadOnlyList<QueueBlock> GetBlocks();
}

/// <summary><see cref="QueueBlock.PausedByUser"/> while the <see cref="QueueActor"/> is paused.</summary>
public sealed class PauseQueueBlockProvider(QueueActor queue) : IQueueBlockProvider
{
    public IReadOnlyList<QueueBlock> GetBlocks() => queue.IsPaused ? [QueueBlock.PausedByUser] : [];
}
