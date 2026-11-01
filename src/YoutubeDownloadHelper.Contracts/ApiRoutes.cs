namespace YoutubeDownloadHelper.Contracts;

/// <summary>
/// The engine API's routes (PLAN.md §6). Every route is absolute, relative to the discovery file's
/// <see cref="Discovery.EngineDiscovery.BaseUrl"/>, and needs <c>Authorization: Bearer &lt;token&gt;</c>.
/// </summary>
public static class ApiRoutes
{
    /// <summary>The API version in the routes and in <see cref="Discovery.EngineDiscovery.ApiVersion"/>.</summary>
    public const int Version = 1;

    public const string Base = "/api/v1";

    /// <summary><c>GET</c>: an <see cref="Info.EngineInfo"/>.</summary>
    public const string Info = Base + "/info";

    /// <summary><c>POST</c>: stops the engine gracefully. Answers <c>202 Accepted</c> before it stops.</summary>
    public const string EngineShutdown = Base + "/engine/shutdown";

    /// <summary>The OpenAPI document, for non-.NET clients.</summary>
    public const string OpenApiDocument = "/openapi/v1.json";
}
