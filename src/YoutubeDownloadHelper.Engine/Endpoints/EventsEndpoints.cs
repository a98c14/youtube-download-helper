using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Engine.Events;

namespace YoutubeDownloadHelper.Engine.Endpoints;

public static class EventsEndpoints
{
    /// <summary>The header (or query parameter <c>lastEventId</c>) with the id of the last event a client saw.</summary>
    public const string LastEventIdHeader = "Last-Event-ID";

    public static IEndpointRouteBuilder MapEventsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ApiRoutes.Events, StreamAsync)
            .WithTags("Events")
            .WithName("streamEvents")
            .WithSummary("Server-sent events: every change to the queue, the blocks and the tools, and notifications.")
            .WithDescription(
                "Each event has an id (the sequence number), an event name and one JSON object as data (see the Contracts " +
                "EventTypes). Send Last-Event-ID (header, or the lastEventId query parameter) to get every later event; " +
                "if they are no longer buffered, or without it, the stream starts with 'resync'. Comment lines are " +
                "heartbeats. The stream ends after 'engine.stopping'.")
            .Produces<string>(StatusCodes.Status200OK, "text/event-stream");
        return endpoints;
    }

    private static async Task StreamAsync(
        HttpContext context,
        EventJournal journal,
        EventStreamOptions options,
        TimeProvider time)
    {
        var lastEventId = ReadLastEventId(context.Request);
        var response = context.Response;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await response.StartAsync(context.RequestAborted);

        using var connection = journal.Connect(lastEventId);
        try
        {
            await EventStreamWriter.WriteAsync(response, connection, options.HeartbeatInterval, time, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away.
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away mid-write.
        }
    }

    private static long? ReadLastEventId(HttpRequest request)
    {
        var text = request.Headers.TryGetValue(LastEventIdHeader, out var header) && !string.IsNullOrWhiteSpace(header)
            ? header.ToString()
            : request.Query["lastEventId"].ToString();
        return long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}
