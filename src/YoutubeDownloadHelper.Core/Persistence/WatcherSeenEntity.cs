namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>A video a watcher has already seen, so it is not queued again (table <c>watcher_seen</c>, PLAN.md §5.5).</summary>
public sealed class WatcherSeenEntity
{
    public Guid WatcherId { get; set; }

    public required string Extractor { get; set; }

    public required string VideoId { get; set; }

    public DateTimeOffset SeenAt { get; set; }
}
