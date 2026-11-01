using System.Text.Json;
using YoutubeDownloadHelper.Contracts.Presets;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Presets;
using YoutubeDownloadHelper.Core.Runner;
using YoutubeDownloadHelper.Core.Tests.FakeYtDlp;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Tests.Runner;

/// <summary>Runs the built commands against <c>YoutubeDownloadHelper.FakeYtDlp</c>, so the command lines stay runnable.</summary>
public sealed class YtDlpCommandFakeRunTests : IDisposable
{
    private readonly FakeYtDlpFolder _folder = new();
    private readonly YtDlpCommandBuilder _builder;

    public YtDlpCommandFakeRunTests()
    {
        var paths = new DataPaths(_folder.In("data"));
        _builder = new YtDlpCommandBuilder(
            paths,
            new ConfiguredToolLocator(paths, new ToolLocatorOptions { YtDlp = FakeYtDlpProcess.ExePath }),
            new PresetRegistry());
    }

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(PresetIds.Best, "mp4")]
    [InlineData(PresetIds.P1080, "mp4")]
    [InlineData(PresetIds.AudioM4a, "m4a")]
    [InlineData(PresetIds.AudioMp3, "mp3")]
    public async Task A_download_ends_in_the_channel_folder_of_the_destination(string presetId, string ext)
    {
        var moved = await DownloadAsync(Request(presetId));

        Assert.Equal(Path.Combine(_folder.Home, "Fake Channel", $"2026-01-02 - İzmir 😀.{ext}"), moved);
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public async Task The_video_id_suffix_and_a_folder_override_change_the_final_path()
    {
        var moved = await DownloadAsync(Request(PresetIds.Best) with { AppendVideoId = true, FolderOverride = "Arşiv İ" });

        Assert.Equal(Path.Combine(_folder.Home, "Arşiv İ", "Fake Channel", "2026-01-02 - İzmir 😀 [fksuccess00].mp4"), moved);
    }

    [Fact]
    public async Task The_auth_verification_succeeds_only_when_logged_in()
    {
        var jar = _folder.In("cookies.txt");
        await File.WriteAllTextAsync(jar, "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t1893456000\tPREF\thl=tr\n", TestContext.Current.CancellationToken);
        Assert.Equal(1, (await RunAsync(_builder.VerifyAuth(jar))).ExitCode);

        await File.WriteAllTextAsync(jar, "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t1893456000\tLOGIN_INFO\tlogin\n", TestContext.Current.CancellationToken);
        var result = await RunAsync(_builder.VerifyAuth(jar));

        Assert.Equal(0, result.ExitCode);
        Assert.Single(JsonDocument.Parse(string.Concat(result.Stdout)).RootElement.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task A_flat_listing_returns_the_first_entries()
    {
        var result = await RunAsync(_builder.FlatListing("https://fake.test/channel/videos?count=40"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(YtDlpCommandBuilder.DefaultListingEnd, JsonDocument.Parse(string.Concat(result.Stdout)).RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task A_resolve_returns_one_video()
    {
        var result = await RunAsync(_builder.Resolve("https://fake.test/success?id=abcdefghijk"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("abcdefghijk", JsonDocument.Parse(string.Concat(result.Stdout)).RootElement.GetProperty("id").GetString());
    }

    private DownloadRequest Request(string presetId) => new()
    {
        ItemId = 7,
        Url = "https://fake.test/success?title=%C4%B0zmir%20%F0%9F%98%80",
        PresetId = presetId,
        DownloadFolder = _folder.Home,
    };

    private async Task<string> DownloadAsync(DownloadRequest request)
    {
        var result = await RunAsync(_builder.Download(request));

        Assert.Equal(0, result.ExitCode);
        var moved = JsonDocument.Parse(Assert.Single(result.Lines("moved"))[YtDlpTemplates.LinePrefix.Length..]);
        return moved.RootElement.GetProperty("i").GetProperty("filepath").GetString()!;
    }

    private static Task<FakeYtDlpResult> RunAsync(YtDlpCommand command)
    {
        Assert.Equal(FakeYtDlpProcess.ExePath, command.FileName);
        return FakeYtDlpProcess.RunAsync(command.Arguments, encoding: null);
    }
}
