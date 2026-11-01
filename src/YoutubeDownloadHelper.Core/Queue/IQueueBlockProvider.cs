using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Core.Queue;

/// <summary>
/// The queue-level blocks (PLAN.md §4.3) as <c>GET /info</c> reports them. <see cref="PauseQueueBlockProvider"/> is
/// the default; the scheduler (#42) replaces it with one that also knows network, disk and tool blocks.
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
