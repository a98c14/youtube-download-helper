namespace YoutubeDownloadHelper.Contracts.Discovery;

/// <summary>
/// The discovery file <c>engine.json</c> in the data folder (PLAN.md §6.1): how other programs find and call the
/// running engine. The engine writes it once it is listening, readable only by the user, and deletes it on a clean
/// shutdown. Treat it as stale when <see cref="Pid"/> is gone or <see cref="ApiRoutes.Info"/> fails.
/// </summary>
/// <param name="ApiVersion">The API version (<see cref="ApiRoutes.Version"/>).</param>
/// <param name="BaseUrl">E.g. <c>http://127.0.0.1:47815</c>, without a trailing slash.</param>
/// <param name="Token">The bearer token. Regenerated on every engine start.</param>
/// <param name="Pid">The engine's process ID.</param>
/// <param name="EngineVersion">The engine's version, e.g. <c>1.0.0</c>.</param>
public sealed record EngineDiscovery(int ApiVersion, string BaseUrl, string Token, int Pid, string EngineVersion)
{
    /// <summary>The file name in the data folder (<see cref="AppIdentity.DataFolderName"/>).</summary>
    public const string FileName = "engine.json";
}
