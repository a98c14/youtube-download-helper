using Microsoft.EntityFrameworkCore;
using YoutubeDownloadHelper.Contracts.Resolve;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Scheduling;

namespace YoutubeDownloadHelper.Engine.Endpoints;

/// <summary>
/// Finds what would make an add a duplicate (PLAN.md §6.2): the same URL or YouTube video ID in the queue, or the video
/// in the download archive. Shared by <c>POST /items</c> and <c>POST /resolve</c>, so the picker's flags match what an
/// add does.
/// </summary>
public sealed class DuplicateFinder(QueueActor queue, IDbContextFactory<StateDbContext> contextFactory)
{
    /// <summary>The queue as it is now, and the archive entries of <paramref name="videoIds"/> (YouTube video IDs).</summary>
    public async Task<DuplicateIndex> LoadAsync(IEnumerable<string> videoIds, CancellationToken cancellationToken)
    {
        var snapshot = await queue.GetSnapshotAsync(cancellationToken);
        var queuedByUrl = new Dictionary<string, long>(StringComparer.Ordinal);
        var queuedByVideo = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items)
        {
            queuedByUrl.TryAdd(item.Url, item.Id);
            if (item.VideoId is { } id && string.Equals(item.Extractor, YouTubeUrls.Extractor, StringComparison.Ordinal))
            {
                queuedByVideo.TryAdd(id, item.Id);
            }
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var downloaded = await db.GetDownloadedItemIdsAsync(YouTubeUrls.Extractor, videoIds, cancellationToken);
        return new DuplicateIndex(queuedByUrl, queuedByVideo, downloaded);
    }
}

/// <summary>A point-in-time view of the queue and part of the archive, from <see cref="DuplicateFinder"/>.</summary>
public sealed class DuplicateIndex(
    IReadOnlyDictionary<string, long> queuedByUrl,
    IReadOnlyDictionary<string, long> queuedByVideo,
    IReadOnlyDictionary<string, long> downloaded)
{
    /// <summary>Why a video would be a duplicate, and the item it duplicates; null when it would not be.</summary>
    /// <param name="url">The URL it would be queued with.</param>
    /// <param name="videoId">Its YouTube video ID, when known.</param>
    public (EntryDuplicate Kind, long ItemId)? Find(string url, string? videoId)
    {
        if ((videoId is not null && queuedByVideo.TryGetValue(videoId, out var queuedId)) || queuedByUrl.TryGetValue(url, out queuedId))
        {
            return (EntryDuplicate.Queued, queuedId);
        }

        if (videoId is not null && downloaded.TryGetValue(videoId, out var downloadedId))
        {
            return (EntryDuplicate.Downloaded, downloadedId);
        }

        return null;
    }
}
