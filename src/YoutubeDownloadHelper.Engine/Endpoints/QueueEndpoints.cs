using Microsoft.AspNetCore.Http.HttpResults;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Core.Queue;

namespace YoutubeDownloadHelper.Engine.Endpoints;

public static class QueueEndpoints
{
    public static IEndpointRouteBuilder MapQueueEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ApiRoutes.QueuePause, PauseAsync)
            .WithTags("Queue")
            .WithName("pauseQueue")
            .WithSummary("Pauses the whole queue: no download starts, running ones go back to the queue (partial files kept).");
        endpoints.MapPost(ApiRoutes.QueueResume, ResumeAsync)
            .WithTags("Queue")
            .WithName("resumeQueue")
            .WithSummary("Resumes the whole queue.");
        return endpoints;
    }

    private static async Task<NoContent> PauseAsync(QueueActor queue, CancellationToken cancellationToken)
    {
        await queue.PauseAllAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ResumeAsync(QueueActor queue, CancellationToken cancellationToken)
    {
        await queue.ResumeAllAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
