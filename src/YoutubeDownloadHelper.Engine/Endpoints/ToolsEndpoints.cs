using Microsoft.AspNetCore.Http.HttpResults;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Tools;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Engine.Endpoints;

public static class ToolsEndpoints
{
    public static IEndpointRouteBuilder MapToolsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tools = endpoints.MapGroup(string.Empty).WithTags("Tools");
        tools.MapGet(ApiRoutes.Tools, GetAsync)
            .WithName("getTools")
            .WithSummary("The managed tools: current and previous versions, yt-dlp's channel, last update checks.");
        tools.MapPost(ApiRoutes.ToolsCheck, Check)
            .WithName("checkTools")
            .WithSummary("Checks every tool (smoke tests, then updates) in the background; tools.changed follows when it is done.");
        tools.MapPost(ApiRoutes.ToolRollback, RollbackAsync)
            .WithName("rollbackTool")
            .WithSummary("Makes the tool's previous version current again; update checks skip the version it leaves.");
        return endpoints;
    }

    private static async Task<Ok<ToolList>> GetAsync(ToolManager tools, CancellationToken cancellationToken) =>
        TypedResults.Ok(await tools.GetDetailsAsync(cancellationToken));

    private static Accepted Check(ToolManager tools)
    {
        tools.RequestUpdateCheck();
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<NoContent, NotFound<EngineMessage>, Conflict<EngineMessage>>> RollbackAsync(
        string tool,
        ToolManager tools,
        CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["tool"] = tool };
        return await tools.RollbackAsync(tool, cancellationToken) switch
        {
            ToolRollbackResult.RolledBack => TypedResults.NoContent(),
            ToolRollbackResult.UnknownTool => TypedResults.NotFound(new EngineMessage(ToolCodes.UnknownTool, args, $"There is no tool '{tool}'.")),
            ToolRollbackResult.NotManaged => TypedResults.Conflict(new EngineMessage(
                ToolCodes.NotManaged, args, $"{tool} has a path override in the engine configuration, so the engine does not manage it.")),
            ToolRollbackResult.NoPreviousVersion => TypedResults.Conflict(new EngineMessage(
                ToolCodes.NoPreviousVersion, args, $"{tool} has no previous version to roll back to.")),
            ToolRollbackResult.PreviousVersionBroken => TypedResults.Conflict(new EngineMessage(
                ToolCodes.PreviousVersionBroken, args, $"The previous version of {tool} is incomplete or does not run.")),
            var other => throw new InvalidOperationException($"Unexpected rollback result {other}."),
        };
    }
}
