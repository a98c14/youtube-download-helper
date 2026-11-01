namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Optional tool path overrides, e.g. bound from the engine's configuration to point it at
/// <c>YoutubeDownloadHelper.FakeYtDlp.exe</c> during UI development. A path left <see langword="null"/> uses the
/// default under the data folder's <c>tools\</c>.
/// </summary>
public sealed record ToolLocatorOptions
{
    public string? YtDlp { get; init; }

    public string? Ffmpeg { get; init; }

    public string? Deno { get; init; }
}
