using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Endpoints;

public static class ItemsEndpoints
{
    public const int DefaultPageSize = 1000;

    public const int MaxPageSize = 5000;

    public static IEndpointRouteBuilder MapItemsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var items = endpoints.MapGroup(string.Empty).WithTags("Items");

        items.MapGet(ApiRoutes.Items, ListAsync)
            .WithName("listItems")
            .WithSummary("The queue in order, with live progress, and the event sequence number it is consistent with.")
            .WithDescription(
                "state: comma-separated item states to keep. search: matches title, channel or URL (ignoring case). " +
                "cursor: the nextCursor of the previous page. limit: page size (default 1000, at most 5000).");
        items.MapPost(ApiRoutes.Items, AddAsync)
            .WithName("addItems")
            .WithSummary("Queues URLs or YouTube video IDs, with a result per input: added, duplicate or invalid.");
        items.MapGet(ApiRoutes.Item, GetAsync)
            .WithName("getItem")
            .WithSummary("An item in the queue or in history, with its transitions, its yt-dlp runs and their output.");
        items.MapDelete(ApiRoutes.Item, RemoveAsync)
            .WithName("removeItem")
            .WithSummary("Removes an item from the queue or history. With deleteFiles=true its downloaded file is deleted too.");
        items.MapPost(ApiRoutes.ItemPause, (long id, QueueActor queue, CancellationToken ct) => RunAsync(queue.PauseAsync(id, ct)))
            .WithName("pauseItem")
            .WithSummary("Pauses an item; a running download stops and keeps its partial files.");
        items.MapPost(ApiRoutes.ItemResume, (long id, QueueActor queue, CancellationToken ct) => RunAsync(queue.ResumeAsync(id, ct)))
            .WithName("resumeItem")
            .WithSummary("Puts a paused item back in the queue.");
        items.MapPost(ApiRoutes.ItemRetry, (long id, QueueActor queue, CancellationToken ct) => RunAsync(queue.RetryAsync(id, ct)))
            .WithName("retryItem")
            .WithSummary("Requeues a failed or waiting item, with its retry count reset.");
        items.MapPost(ApiRoutes.ItemMove, MoveAsync)
            .WithName("moveItem")
            .WithSummary("Moves an item to the top, the bottom, or before another item.");
        return endpoints;
    }

    /// <summary>204, or the rejection: 404 for an unknown item, 409 otherwise. (400 is for the callers' own checks.)</summary>
    internal static async Task<Results<NoContent, BadRequest<EngineMessage>, NotFound<EngineMessage>, Conflict<EngineMessage>>> RunAsync(Task<QueueResult> command)
    {
        var result = await command;
        return result.Rejection switch
        {
            null => TypedResults.NoContent(),
            { Code: QueueCodes.ItemNotFound } rejection => TypedResults.NotFound(rejection),
            var rejection => TypedResults.Conflict(rejection),
        };
    }

    private static async Task<Results<Ok<ItemList>, BadRequest<EngineMessage>>> ListAsync(
        QueueActor queue,
        string? state,
        string? search,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        HashSet<ItemState>? states = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            states = [];
            foreach (var name in state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ParseState(name) is not { } parsed)
                {
                    return TypedResults.BadRequest(ApiErrors.InvalidParameter("state", $"Unknown item state '{name}'."));
                }

                states.Add(parsed);
            }
        }

        long after = 0;
        if (cursor is not null && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0))
        {
            return TypedResults.BadRequest(ApiErrors.InvalidParameter("cursor", "The cursor is not one this endpoint returned."));
        }

        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            return TypedResults.BadRequest(ApiErrors.InvalidParameter("limit", $"The limit must be between 1 and {MaxPageSize.ToString(CultureInfo.InvariantCulture)}."));
        }

        var snapshot = await queue.GetSnapshotAsync(cancellationToken);
        var term = search?.Trim();
        var page = snapshot.Items
            .Where(i => i.Position > after)
            .Where(i => states is null || states.Contains(i.State))
            .Where(i => string.IsNullOrEmpty(term) || Matches(i, term))
            .Take(pageSize + 1)
            .ToList();

        string? next = null;
        if (page.Count > pageSize)
        {
            page.RemoveAt(pageSize);
            next = page[^1].Position.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Ok(new ItemList(
            [.. page.Select(i => ApiMapper.ToInfo(i, snapshot.Progress.GetValueOrDefault(i.Id)))],
            snapshot.Sequence,
            next));
    }

    private static async Task<Results<Ok<AddItemsResponse>, BadRequest<EngineMessage>>> AddAsync(
        AddItemsRequest request,
        ItemAdder adder,
        CancellationToken cancellationToken)
    {
        var (response, invalid) = await adder.AddAsync(request, cancellationToken);
        return response is null ? TypedResults.BadRequest(invalid) : TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<ItemDetail>, NotFound<EngineMessage>>> GetAsync(
        long id,
        QueueActor queue,
        IDbContextFactory<StateDbContext> contextFactory,
        CancellationToken cancellationToken)
    {
        ItemEntity? item;
        List<ItemEventEntity> events;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            item = await db.FindVisibleItemAsync(id, cancellationToken);
            if (item is null)
            {
                return TypedResults.NotFound(QueueResult.NotFound(id).Rejection);
            }

            events = await db.GetItemEventsAsync(id, cancellationToken);
        }

        ItemProgress? progress = null;
        if (ItemStateMachine.IsRunning(item.State))
        {
            (await queue.GetSnapshotAsync(cancellationToken)).Progress.TryGetValue(id, out progress);
        }

        var attempts = ApiMapper.ToAttempts(events);
        var outputTail = attempts.LastOrDefault(a => a.OutputTail.Count > 0)?.OutputTail ?? [];
        return TypedResults.Ok(new ItemDetail(
            ApiMapper.ToInfo(QueueItem.From(item), progress),
            [.. events.Select(ApiMapper.ToInfo)],
            attempts,
            outputTail));
    }

    private static Task<Results<NoContent, BadRequest<EngineMessage>, NotFound<EngineMessage>, Conflict<EngineMessage>>> RemoveAsync(
        long id,
        [FromQuery] bool? deleteFiles,
        QueueActor queue,
        CancellationToken cancellationToken) =>
        RunAsync(queue.RemoveAsync(id, deleteFiles ?? false, cancellationToken));

    private static async Task<Results<NoContent, BadRequest<EngineMessage>, NotFound<EngineMessage>, Conflict<EngineMessage>>> MoveAsync(
        long id,
        MoveItemRequest request,
        QueueActor queue,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.To))
        {
            return TypedResults.BadRequest(ApiErrors.InvalidParameter("to", "Move to top, bottom or before."));
        }

        return await RunAsync(queue.MoveAsync([id], request.To, request.BeforeId, cancellationToken));
    }

    private static ItemState? ParseState(string name)
    {
        foreach (var value in Enum.GetValues<ItemState>())
        {
            if (string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static bool Matches(QueueItem item, string term) =>
        (item.Title?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || (item.Channel?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || item.Url.Contains(term, StringComparison.OrdinalIgnoreCase);
}
