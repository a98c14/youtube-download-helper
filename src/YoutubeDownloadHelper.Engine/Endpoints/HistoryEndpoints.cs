using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Endpoints;

public static class HistoryEndpoints
{
    public const int MaxPageSize = 500;

    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ApiRoutes.History, GetAsync)
            .WithTags("History")
            .WithName("getHistory")
            .WithSummary("Completed and skipped items, newest first.")
            .WithDescription(
                "search: matches title, channel or URL (ignoring case for ASCII letters). cursor: the nextCursor of the " +
                "previous page. limit: page size (default 50, at most 500).");
        return endpoints;
    }

    /// <summary>A cursor is <c>&lt;updatedAt as Unix milliseconds&gt;_&lt;id&gt;</c> of the last item of the previous page.</summary>
    public static string FormatCursor(HistoryCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return string.Create(CultureInfo.InvariantCulture, $"{cursor.UpdatedAt.ToUnixTimeMilliseconds()}_{cursor.Id}");
    }

    public static HistoryCursor? ParseCursor(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split('_');
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && ms >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
            && ms <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                ? new HistoryCursor(DateTimeOffset.FromUnixTimeMilliseconds(ms), id)
                : null;
    }

    private static async Task<Results<Ok<HistoryList>, BadRequest<EngineMessage>>> GetAsync(
        string? search,
        string? cursor,
        int? limit,
        IDbContextFactory<StateDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        HistoryCursor? after = null;
        if (cursor is not null && (after = ParseCursor(cursor)) is null)
        {
            return TypedResults.BadRequest(ApiErrors.InvalidParameter("cursor", "The cursor is not one this endpoint returned."));
        }

        var pageSize = limit ?? ItemQueries.DefaultHistoryPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            return TypedResults.BadRequest(ApiErrors.InvalidParameter("limit", $"The limit must be between 1 and {MaxPageSize.ToString(CultureInfo.InvariantCulture)}."));
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var page = await db.GetHistoryPageAsync(search, after, pageSize, cancellationToken);
        return TypedResults.Ok(new HistoryList(
            [.. page.Items.Select(i => ApiMapper.ToInfo(QueueItem.From(i)))],
            page.Next is null ? null : FormatCursor(page.Next)));
    }
}
