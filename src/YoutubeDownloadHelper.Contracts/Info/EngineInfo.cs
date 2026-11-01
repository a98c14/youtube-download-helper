using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Tools;

namespace YoutubeDownloadHelper.Contracts.Info;

/// <summary>The answer to <c>GET</c> <see cref="ApiRoutes.Info"/> (PLAN.md §6.2). Also the engine's health check.</summary>
/// <param name="ApiVersion">The API version (<see cref="ApiRoutes.Version"/>).</param>
/// <param name="EngineVersion">The engine's version, e.g. <c>1.0.0</c>.</param>
/// <param name="Tools">The managed tools.</param>
/// <param name="Auth">The YouTube login status.</param>
/// <param name="Blocks">The conditions currently stopping new downloads; empty when the queue runs.</param>
public sealed record EngineInfo(
    int ApiVersion,
    string EngineVersion,
    IReadOnlyList<ToolInfo> Tools,
    AuthInfo Auth,
    IReadOnlyList<QueueBlock> Blocks);
