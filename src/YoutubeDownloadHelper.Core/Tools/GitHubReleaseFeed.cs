using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Tools;

namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Latest releases from GitHub (PLAN.md §5.6), through the unauthenticated REST API:
/// <list type="bullet">
/// <item><c>yt-dlp/yt-dlp</c>: <c>yt-dlp.exe</c>, checksums in <c>SHA2-256SUMS</c>; the version is the tag.</item>
/// <item><c>denoland/deno</c>: <c>deno-x86_64-pc-windows-msvc.zip</c>, checksum in <c>&lt;asset&gt;.sha256sum</c>
/// (PowerShell <c>Get-FileHash</c> format); the version is the tag without its <c>v</c>.</item>
/// <item><c>yt-dlp/FFmpeg-Builds</c>: <c>ffmpeg-master-latest-win64-gpl.zip</c>, checksums in <c>checksums.sha256</c>.
/// Its tag is always <c>latest</c>, so the version is the release's UTC publish time, <c>yyyy-MM-dd-HHmm</c>.</item>
/// </list>
/// </summary>
public sealed class GitHubReleaseFeed : IToolReleaseFeed, IDisposable
{
    /// <summary>How long a request may wait for an answer, and a download for its next bytes.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private const long MaxTextBytes = 1024 * 1024;

    private static readonly Uri ApiBase = new("https://api.github.com/");

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public GitHubReleaseFeed()
        : this(new HttpClient(), ownsClient: true)
    {
    }

    /// <param name="client">Sends every request; tests give it a fake handler.</param>
    /// <param name="ownsClient">Whether <see cref="Dispose"/> disposes <paramref name="client"/>.</param>
    public GitHubReleaseFeed(HttpClient client, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownsClient = ownsClient;

        // Downloads take as long as they take; StallTimeout guards each wait instead.
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<ToolRelease> GetLatestAsync(ManagedTool tool, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var source = SourceFor(tool);

        using var request = Request(new Uri(ApiBase, $"repos/{source.Repository}/releases/latest"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var timeout = WithTimeout(cancellationToken);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        try
        {
            return Parse(source, document.RootElement);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ToolFeedException($"The latest {tool.Name} release on GitHub could not be read.", ex);
        }
    }

    public async Task<string> GetTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = Request(uri);
        using var timeout = WithTimeout(cancellationToken);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxTextBytes)
        {
            throw new HttpRequestException($"{uri} is too large for a checksum file.");
        }

        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    public async Task DownloadAsync(Uri uri, Stream destination, Action<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var request = Request(uri);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Invoke(received);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"The download of {uri} stalled.", ex);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static ToolRelease Parse(ReleaseSource source, JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? throw new FormatException("The release has no tag.");
        string? assetUrl = null, checksumUrl = null;
        long? size = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, source.Asset, StringComparison.Ordinal))
            {
                assetUrl = asset.GetProperty("browser_download_url").GetString();
                size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
            }
            else if (string.Equals(name, source.ChecksumAsset, StringComparison.Ordinal))
            {
                checksumUrl = asset.GetProperty("browser_download_url").GetString();
            }
        }

        if (assetUrl is null || checksumUrl is null)
        {
            throw new ToolFeedException(
                $"Release {tag} of {source.Repository} has no {(assetUrl is null ? source.Asset : source.ChecksumAsset)}.");
        }

        var version = source.Version switch
        {
            VersionSource.Tag => tag,
            VersionSource.TagWithoutV => tag.StartsWith('v') ? tag[1..] : tag,
            _ => release.GetProperty("published_at").GetDateTimeOffset().UtcDateTime.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture),
        };
        return new ToolRelease(version, source.Asset, new Uri(assetUrl), size, new Uri(checksumUrl), source.Channel);
    }

    private static ReleaseSource SourceFor(ManagedTool tool) => tool.Name switch
    {
        ToolNames.YtDlp => new("yt-dlp/yt-dlp", "yt-dlp.exe", "SHA2-256SUMS", VersionSource.Tag, "stable"),
        ToolNames.Deno => new("denoland/deno", "deno-x86_64-pc-windows-msvc.zip", "deno-x86_64-pc-windows-msvc.zip.sha256sum", VersionSource.TagWithoutV, null),
        ToolNames.Ffmpeg => new("yt-dlp/FFmpeg-Builds", "ffmpeg-master-latest-win64-gpl.zip", "checksums.sha256", VersionSource.PublishedAt, null),
        _ => throw new ArgumentException($"Unknown tool {tool.Name}.", nameof(tool)),
    };

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        // GitHub's API rejects requests without a User-Agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.InternalName, "1"));
        return request;
    }

    private static CancellationTokenSource WithTimeout(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(StallTimeout);
        return cts;
    }

    private enum VersionSource
    {
        Tag,
        TagWithoutV,
        PublishedAt,
    }

    private sealed record ReleaseSource(string Repository, string Asset, string ChecksumAsset, VersionSource Version, string? Channel);
}
