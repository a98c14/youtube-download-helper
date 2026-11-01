namespace YoutubeDownloadHelper.Contracts.Discovery;

/// <summary>The exit codes of <see cref="AppIdentity.EngineExecutableName"/>.</summary>
public static class EngineExitCodes
{
    /// <summary>A clean shutdown.</summary>
    public const int Success = 0;

    /// <summary>The engine failed to start or crashed; details are in the engine log.</summary>
    public const int Failed = 1;

    /// <summary>
    /// Another engine already runs for this Windows user (and data folder), so this one exited at once. Use the
    /// discovery file to reach it.
    /// </summary>
    public const int AlreadyRunning = 3;
}
