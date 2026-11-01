namespace YoutubeDownloadHelper.Contracts.Tools;

/// <summary>
/// Message codes of a failed tool install (PLAN.md §5.6). Every one has the arg <c>tool</c> (one of
/// <see cref="ToolNames"/>); <see cref="SmokeTestFailed"/> also has <c>file</c>.
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

    /// <summary>The new binary did not run (<c>--version</c>).</summary>
    public const string SmokeTestFailed = "tool.install.smokeTestFailed";

    /// <summary>Anything else, e.g. the disk is full or the folder cannot be written.</summary>
    public const string Failed = "tool.install.failed";
}
