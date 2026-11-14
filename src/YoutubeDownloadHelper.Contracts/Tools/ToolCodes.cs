namespace YoutubeDownloadHelper.Contracts.Tools;

/// <summary>
/// Message codes of a failed tool install and of the tools API (PLAN.md §5.6). Every one has the arg <c>tool</c> (one
/// of <see cref="ToolNames"/>, or for <see cref="UnknownTool"/> the name asked for); <see cref="SmokeTestFailed"/> also
/// has <c>file</c>.
/// </summary>
public static class ToolCodes
{
    /// <summary>The release feed or the download could not be reached.</summary>
    public const string DownloadFailed = "tool.install.downloadFailed";

    /// <summary>The release has no asset for Windows, or its archive lacks a binary.</summary>
    public const string AssetMissing = "tool.install.assetMissing";

    /// <summary>The release publishes no SHA-256 checksum for the asset.</summary>
    public const string ChecksumMissing = "tool.install.checksumMissing";

    /// <summary>The downloaded file does not match the published SHA-256 checksum.</summary>
    public const string ChecksumMismatch = "tool.install.checksumMismatch";

    /// <summary>The new binary did not run (<c>--version</c>), or yt-dlp could not simulate downloading a test video.</summary>
    public const string SmokeTestFailed = "tool.install.smokeTestFailed";

    /// <summary>Anything else, e.g. the disk is full or the folder cannot be written.</summary>
    public const string Failed = "tool.install.failed";

    /// <summary>404: there is no tool by that name.</summary>
    public const string UnknownTool = "tool.unknown";

    /// <summary>409: the tool has a path override in the engine configuration, so the engine does not manage it.</summary>
    public const string NotManaged = "tool.notManaged";

    /// <summary>409: a rollback was asked for, but there is no previous version.</summary>
    public const string NoPreviousVersion = "tool.rollback.noPreviousVersion";

    /// <summary>409: a rollback was asked for, but the previous version is incomplete or does not run.</summary>
    public const string PreviousVersionBroken = "tool.rollback.previousVersionBroken";
}
