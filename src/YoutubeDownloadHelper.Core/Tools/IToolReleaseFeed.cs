namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Where tool releases come from, and all of the tool manager's HTTP access. <see cref="GitHubReleaseFeed"/> is the
/// default; tests use a fake feed (no real network in automated tests).
/// </summary>
public interface IToolReleaseFeed
{
    /// <summary>The newest stable release of <paramref name="tool"/> for Windows x64.</summary>
    /// <exception cref="ToolFeedException">The release has no usable asset or checksum file.</exception>
    /// <exception cref="HttpRequestException">The feed could not be reached.</exception>
    Task<ToolRelease> GetLatestAsync(ManagedTool tool, CancellationToken cancellationToken);

    /// <summary>Downloads a small text file, e.g. a release's checksum file.</summary>
    /// <exception cref="HttpRequestException">The file could not be downloaded.</exception>
    Task<string> GetTextAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads <paramref name="uri"/> into <paramref name="destination"/>, calling <paramref name="progress"/> with the
    /// bytes received so far.
    /// </summary>
    /// <exception cref="HttpRequestException">The download failed or stalled.</exception>
    /// <exception cref="IOException">The download failed or stalled.</exception>
    Task DownloadAsync(Uri uri, Stream destination, Action<long>? progress, CancellationToken cancellationToken);
}

/// <summary>One release of a tool.</summary>
/// <param name="Version">Also the version folder's name, e.g. <c>2026.10.26</c>.</param>
/// <param name="AssetName">The file to download: the binary itself or a zip that holds it.</param>
/// <param name="AssetUri">Where to download it.</param>
/// <param name="Size">The asset's size in bytes, when the feed knows it.</param>
/// <param name="ChecksumUri">The release's published SHA-256 checksum file (<c>sha256sum</c> or PowerShell format).</param>
/// <param name="Channel"><c>stable</c> for yt-dlp; <see langword="null"/> for tools without channels.</param>
public sealed record ToolRelease(
    string Version, string AssetName, Uri AssetUri, long? Size, Uri ChecksumUri, string? Channel);

/// <summary>A release the feed found is not usable (e.g. it lacks the Windows asset).</summary>
public sealed class ToolFeedException : Exception
{
    public ToolFeedException()
    {
    }

    public ToolFeedException(string message)
        : base(message)
    {
    }

    public ToolFeedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
