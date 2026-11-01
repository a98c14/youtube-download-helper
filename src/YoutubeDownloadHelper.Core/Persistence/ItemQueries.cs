using Microsoft.EntityFrameworkCore;
using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>Where history paging continues: after the item with this sort key.</summary>
public sealed record HistoryCursor(DateTimeOffset UpdatedAt, long Id);

public sealed record HistoryPage(IReadOnlyList<ItemEntity> Items, HistoryCursor? Next);

/// <summary>The reads the queue actor and the history endpoint need. Writes go through the actor.</summary>
public static class ItemQueries
{
    public const int DefaultHistoryPageSize = 50;

    /// <summary>States shown in history rather than in the queue (PLAN.md §5.1, §6.2).</summary>
    public static IReadOnlyList<ItemState> HistoryStates => HistoryStateArray;

    // Arrays, because EF translates Contains on arrays (to IN) but not on sets.
    private static readonly ItemState[] HistoryStateArray = [ItemState.Completed, ItemState.Skipped];

    // Removed and seen items are in neither the queue nor history.
    private static readonly ItemState[] InactiveStateArray = [.. HistoryStateArray, ItemState.Removed, ItemState.Seen];

    private const string LikeEscape = "\\";

    /// <summary>Every item not removed, seen or in history, in queue order. Loaded once at startup by the queue actor.</summary>
    public static Task<List<ItemEntity>> LoadActiveAsync(this StateDbContext db, CancellationToken cancellationToken = default) =>
        db.Items
            .Where(i => !InactiveStateArray.Contains(i.State))
            .OrderBy(i => i.Position)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// History, most recently updated first, keyset-paged. Items in history are not expected to change, so
    /// <see cref="Entity.UpdatedAt"/> is when they got there. <paramref name="search"/> matches title, channel or URL (SQLite
    /// <c>LIKE</c>: case-insensitive for ASCII only, which is culture-independent).
    /// </summary>
    public static async Task<HistoryPage> GetHistoryPageAsync(
        this StateDbContext db,
        string? search = null,
        HistoryCursor? after = null,
        int pageSize = DefaultHistoryPageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var query = db.Items.AsNoTracking().Where(i => HistoryStateArray.Contains(i.State));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(i =>
                EF.Functions.Like(i.Title, pattern, LikeEscape)
                || EF.Functions.Like(i.Channel, pattern, LikeEscape)
                || EF.Functions.Like(i.Url, pattern, LikeEscape));
        }

        if (after is not null)
        {
            var (updatedAt, id) = after;
            query = query.Where(i => i.UpdatedAt < updatedAt || (i.UpdatedAt == updatedAt && i.Id < id));
        }

        var items = await query
            .OrderByDescending(i => i.UpdatedAt)
            .ThenByDescending(i => i.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        if (items.Count <= pageSize)
        {
            return new HistoryPage(items, null);
        }

        items.RemoveAt(pageSize);
        var last = items[^1];
        return new HistoryPage(items, new HistoryCursor(last.UpdatedAt, last.Id));
    }

    /// <summary>
    /// The download archive (PLAN.md §5.4): which of <paramref name="videoIds"/> have an item in
    /// <see cref="ItemState.Completed"/>. Removing the item takes the video out of the archive. Ids compare ordinally,
    /// like yt-dlp's archive.
    /// </summary>
    public static Task<HashSet<string>> GetDownloadedVideoIdsAsync(
        this StateDbContext db,
        string extractor,
        IEnumerable<string> videoIds,
        CancellationToken cancellationToken = default) =>
        db.Items.Where(i => i.State == ItemState.Completed).MatchVideoIdsAsync(extractor, videoIds, cancellationToken);

    /// <summary>
    /// Which of <paramref name="videoIds"/> have an item in any state, <see cref="ItemState.Removed"/> and
    /// <see cref="ItemState.Seen"/> included. A watcher queues only videos not in this set (PLAN.md §5.5), so a video
    /// the user removed is not queued again.
    /// </summary>
    public static Task<HashSet<string>> GetKnownVideoIdsAsync(
        this StateDbContext db,
        string extractor,
        IEnumerable<string> videoIds,
        CancellationToken cancellationToken = default) =>
        db.Items.MatchVideoIdsAsync(extractor, videoIds, cancellationToken);

    /// <summary>
    /// For each of <paramref name="videoIds"/> in the download archive, the newest <see cref="ItemState.Completed"/>
    /// item that downloaded it.
    /// </summary>
    public static async Task<Dictionary<string, long>> GetDownloadedItemIdsAsync(
        this StateDbContext db,
        string extractor,
        IEnumerable<string> videoIds,
        CancellationToken cancellationToken = default)
    {
        var ids = videoIds.Distinct(StringComparer.Ordinal).ToArray();
        var rows = await db.Items
            .Where(i => i.State == ItemState.Completed && i.Extractor == extractor && ids.Contains(i.VideoId!))
            .Select(i => new { VideoId = i.VideoId!, i.Id })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result[row.VideoId] = result.TryGetValue(row.VideoId, out var other) ? Math.Max(other, row.Id) : row.Id;
        }

        return result;
    }

    /// <summary>An item in any state except <see cref="ItemState.Removed"/> and <see cref="ItemState.Seen"/>, or null.</summary>
    public static Task<ItemEntity?> FindVisibleItemAsync(this StateDbContext db, long itemId, CancellationToken cancellationToken = default) =>
        db.Items.AsNoTracking()
            .Where(i => i.Id == itemId && i.State != ItemState.Removed && i.State != ItemState.Seen)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>An item's transitions, oldest first.</summary>
    public static Task<List<ItemEventEntity>> GetItemEventsAsync(this StateDbContext db, long itemId, CancellationToken cancellationToken = default) =>
        db.ItemEvents.AsNoTracking()
            .Where(e => e.ItemId == itemId)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken);

    private static async Task<HashSet<string>> MatchVideoIdsAsync(
        this IQueryable<ItemEntity> items,
        string extractor,
        IEnumerable<string> videoIds,
        CancellationToken cancellationToken)
    {
        var ids = videoIds.Distinct(StringComparer.Ordinal).ToArray();
        var matched = await items
            .Where(i => i.Extractor == extractor && ids.Contains(i.VideoId!))
            .Select(i => i.VideoId!)
            .Distinct()
            .ToListAsync(cancellationToken);

        return matched.ToHashSet(StringComparer.Ordinal);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
