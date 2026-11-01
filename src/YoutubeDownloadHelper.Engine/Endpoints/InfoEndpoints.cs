using Microsoft.AspNetCore.Http.HttpResults;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Info;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Tools;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Endpoints;

public static class InfoEndpoints
{
    public static IEndpointRouteBuilder MapInfoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ApiRoutes.Info, GetInfo)
            .WithTags("Info")
            .WithName("getInfo")
            .WithSummary("Engine and API versions, tools, login status and queue blocks. Also the health check.");
        return endpoints;
    }

    private static Ok<EngineInfo> GetInfo(IToolStatusProvider tools, IAuthStatusProvider auth, IQueueBlockProvider blocks) =>
        TypedResults.Ok(new EngineInfo(ApiRoutes.Version, EngineVersion.Current, tools.GetTools(), auth.GetStatus(), blocks.GetBlocks()));
}
