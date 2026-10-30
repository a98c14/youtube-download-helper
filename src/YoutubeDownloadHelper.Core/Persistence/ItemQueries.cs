using Microsoft.EntityFrameworkCore;
using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>Where history paging continues: after the item with this sort key.</summary>
public sealed record HistoryCursor(DateTimeOffset FinishedAt, Guid Id);

public sealed record HistoryPage(IReadOnlyList<ItemEntity> Items, HistoryCursor? Next);

/// <summary>The reads the queue actor and the history endpoint need. Writes go through the actor.</summary>
public static class ItemQueries
{
    public const int DefaultHistoryPageSize = 50;

    /// <summary>States shown in history rather than in the queue (PLAN.md §5.1, §6.2), unless the item was removed.</summary>
    public static IReadOnlyList<ItemState> HistoryStates => HistoryStateArray;

    // An array, because EF translates Contains on arrays (to IN) but not on sets.
    private static readonly ItemState[] HistoryStateArray = [ItemState.Completed, ItemState.Skipped];

    private const string LikeEscape = "\\";

    /// <summary>Every item not removed or in history, in queue order. Loaded once at startup by the queue actor.</summary>
    public static Task<List<ItemEntity>> LoadActiveAsync(this StateDbContext db, CancellationToken cancellationToken = default) =>
        db.Items
            .Where(i => i.RemovedAt == null && !HistoryStateArray.Contains(i.State))
            .OrderBy(i => i.Position)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// History, newest first, keyset-paged. <paramref name="search"/> matches title, channel or URL (SQLite
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

        var query = db.Items.AsNoTracking().Where(i => i.RemovedAt == null && HistoryStateArray.Contains(i.State) && i.FinishedAt != null);

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
            var (finishedAt, id) = after;
            query = query.Where(i => i.FinishedAt < finishedAt || (i.FinishedAt == finishedAt && i.Id.CompareTo(id) < 0));
        }

        var items = await query
            .OrderByDescending(i => i.FinishedAt)
            .ThenByDescending(i => i.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        if (items.Count <= pageSize)
        {
            return new HistoryPage(items, null);
        }

        items.RemoveAt(pageSize);
        var last = items[^1];
        return new HistoryPage(items, new HistoryCursor(last.FinishedAt!.Value, last.Id));
    }

    /// <summary>
    /// The download archive (PLAN.md §5.4): which of <paramref name="videoIds"/> some item has downloaded, i.e. has
    /// a <see cref="ItemEntity.FilePath"/>. It still counts after the item is removed or re-queued. Ids compare
    /// ordinally, like yt-dlp's archive.
    /// </summary>
    public static async Task<HashSet<string>> GetDownloadedVideoIdsAsync(
        this StateDbContext db,
        string extractor,
        IEnumerable<string> videoIds,
        CancellationToken cancellationToken = default)
    {
        var ids = videoIds.Distinct(StringComparer.Ordinal).ToArray();
        var downloaded = await db.Items
            .Where(i => i.Extractor == extractor
                && ids.Contains(i.VideoId!)
                && i.FilePath != null)
            .Select(i => i.VideoId!)
            .Distinct()
            .ToListAsync(cancellationToken);

        return downloaded.ToHashSet(StringComparer.Ordinal);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
