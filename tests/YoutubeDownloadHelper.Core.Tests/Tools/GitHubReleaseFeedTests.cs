using System.Net;
using System.Text;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Tests.Tools;

public sealed class GitHubReleaseFeedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_the_latest_yt_dlp_release()
    {
        var handler = new FakeHandler(Release("2026.10.26", "2026-10-26T20:00:00Z", "yt-dlp", "yt-dlp.exe", "SHA2-256SUMS", "yt-dlp_x86.exe"));
        using var feed = new GitHubReleaseFeed(new HttpClient(handler));

        var release = await feed.GetLatestAsync(ManagedTool.YtDlp, Ct);

        Assert.Equal(
            new ToolRelease(
                "2026.10.26", "yt-dlp.exe", new Uri("https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe"), 1234,
                new Uri("https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/SHA2-256SUMS"), "stable"),
            release);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest", request.RequestUri!.ToString());
        Assert.NotEmpty(request.Headers.UserAgent);
    }

    [Fact]
    public async Task Deno_versions_drop_the_v_and_ffmpeg_versions_are_the_publish_time()
    {
        using var deno = new GitHubReleaseFeed(new HttpClient(new FakeHandler(Release(
            "v2.9.8", "2026-10-30T10:00:00Z", "deno", "deno-x86_64-pc-windows-msvc.zip", "deno-x86_64-pc-windows-msvc.zip.sha256sum"))));
        using var ffmpeg = new GitHubReleaseFeed(new HttpClient(new FakeHandler(Release(
            "latest", "2026-11-01T19:09:08Z", "ffmpeg", "ffmpeg-master-latest-win64-gpl.zip", "checksums.sha256", "ffmpeg-master-latest-win64-gpl-shared.zip"))));

        var denoRelease = await deno.GetLatestAsync(ManagedTool.Deno, Ct);
        var ffmpegRelease = await ffmpeg.GetLatestAsync(ManagedTool.Ffmpeg, Ct);

        Assert.Equal(("2.9.8", "deno-x86_64-pc-windows-msvc.zip", null), (denoRelease.Version, denoRelease.AssetName, denoRelease.Channel));
        Assert.Equal(("2026-11-01-1909", "ffmpeg-master-latest-win64-gpl.zip"), (ffmpegRelease.Version, ffmpegRelease.AssetName));
        Assert.EndsWith("/checksums.sha256", ffmpegRelease.ChecksumUri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_release_without_the_windows_asset_is_unusable()
    {
        using var feed = new GitHubReleaseFeed(new HttpClient(new FakeHandler(Release("2026.10.26", "2026-10-26T20:00:00Z", "yt-dlp", "SHA2-256SUMS"))));

        await Assert.ThrowsAsync<ToolFeedException>(() => feed.GetLatestAsync(ManagedTool.YtDlp, Ct));
    }

    [Fact]
    public async Task An_http_error_is_an_HttpRequestException()
    {
        using var feed = new GitHubReleaseFeed(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))));

        await Assert.ThrowsAsync<HttpRequestException>(() => feed.GetLatestAsync(ManagedTool.YtDlp, Ct));
    }

    [Fact]
    public async Task Downloads_into_the_stream_and_reports_progress()
    {
        var bytes = new byte[300_000];
        new Random(1).NextBytes(bytes);
        using var feed = new GitHubReleaseFeed(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
        using var destination = new MemoryStream();
        var reports = new List<long>();

        await feed.DownloadAsync(new Uri("https://github.com/x/y.zip"), destination, reports.Add, Ct);

        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(bytes.Length, reports[^1]);
    }

    [Fact]
    public async Task Gets_a_checksum_file()
    {
        using var feed = new GitHubReleaseFeed(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("abc  yt-dlp.exe\n") })));

        Assert.Equal("abc  yt-dlp.exe\n", await feed.GetTextAsync(new Uri("https://github.com/x/SHA2-256SUMS"), Ct));
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Release(string tag, string publishedAt, string tool, params string[] assets)
    {
        var repo = tool switch { "deno" => "denoland/deno", "ffmpeg" => "yt-dlp/FFmpeg-Builds", _ => "yt-dlp/yt-dlp" };
        var json = new StringBuilder();
        json.Append("{\"tag_name\":\"").Append(tag).Append("\",\"published_at\":\"").Append(publishedAt).Append("\",\"assets\":[");
        json.AppendJoin(',', assets.Select(a =>
            $"{{\"name\":\"{a}\",\"size\":1234,\"browser_download_url\":\"https://github.com/{repo}/releases/download/{tag}/{a}\"}}"));
        json.Append("]}");
        return _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToString(), Encoding.UTF8, "application/json") };
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
