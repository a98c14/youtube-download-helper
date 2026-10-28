namespace YoutubeDownloadHelper.Contracts;

/// <summary>
/// The app's names. <see cref="DisplayName"/> is the only user-facing name; it contains non-ASCII characters,
/// so paths and IDs always use the internal names instead and are never derived from it.
/// </summary>
public static class AppIdentity
{
    public const string DisplayName = "Youtube İndirgeç";

    public const string InternalName = "YoutubeDownloadHelper";

    /// <summary>The per-user data folder under <c>%LOCALAPPDATA%</c> (PLAN.md §8).</summary>
    public const string DataFolderName = "YoutubeDownloadHelperData";

    public const string VelopackPackId = "YoutubeDownloadHelper";

    public const string AppExecutableName = "YoutubeDownloadHelper.exe";

    public const string EngineExecutableName = "YoutubeDownloadHelper.Engine.exe";
}
