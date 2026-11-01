using Microsoft.AspNetCore.Http.HttpResults;
using YoutubeDownloadHelper.Contracts;

namespace YoutubeDownloadHelper.Engine.Endpoints;

public static class EngineEndpoints
{
    public static IEndpointRouteBuilder MapEngineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ApiRoutes.EngineShutdown, Shutdown)
            .WithTags("Engine")
            .WithName("shutdownEngine")
            .WithSummary("Stops the engine gracefully. Answers before it stops.");
        return endpoints;
    }

    // Stopping the host stops the hosted services in reverse order; the scheduler pauses running jobs there.
    private static Accepted Shutdown(IHostApplicationLifetime lifetime)
    {
        lifetime.StopApplication();
        return TypedResults.Accepted((string?)null);
    }
}
