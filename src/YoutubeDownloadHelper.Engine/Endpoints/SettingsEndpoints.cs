using Microsoft.AspNetCore.Http.HttpResults;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Settings;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Engine.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ApiRoutes.Settings, GetAsync)
            .WithTags("Settings")
            .WithName("getSettings")
            .WithSummary("The user's settings.");
        endpoints.MapPatch(ApiRoutes.Settings, PatchAsync)
            .WithTags("Settings")
            .WithName("patchSettings")
            .WithSummary("Changes the settings that are present in the body; all or nothing. Answers the new settings.");
        return endpoints;
    }

    private static async Task<Ok<SettingsInfo>> GetAsync(SettingsStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok(ApiMapper.ToInfo(await store.LoadAsync(cancellationToken)));

    private static async Task<Results<Ok<SettingsInfo>, BadRequest<EngineMessage>>> PatchAsync(
        SettingsPatch patch,
        SettingsEditor editor,
        CancellationToken cancellationToken)
    {
        var (settings, invalid) = await editor.PatchAsync(patch, cancellationToken);
        return settings is null ? TypedResults.BadRequest(invalid) : TypedResults.Ok(ApiMapper.ToInfo(settings));
    }
}
