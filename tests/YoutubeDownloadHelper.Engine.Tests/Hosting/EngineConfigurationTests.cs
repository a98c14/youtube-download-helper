using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Tools;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Tests.Hosting;

public sealed class EngineConfigurationTests
{
    [Fact]
    public async Task Command_line_overrides_port_data_folder_and_tool_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "YoutubeDownloadHelper.Tests", "İndirgeç config");
        var fake = Path.Combine(Path.GetTempPath(), "fake", "YoutubeDownloadHelper.FakeYtDlp.exe");
        var builder = EngineHost.CreateBuilder(["--Engine:Port=0", $"--Engine:DataRoot={root}", $"--Engine:Tools:YtDlp={fake}"]);

        var options = EngineHost.ReadOptions(builder.Configuration);
        var paths = EngineHost.ResolveDataPaths(options);
        await using var app = builder.AddEngine(paths).Build();

        Assert.Equal(0, options.Port);
        Assert.Equal(root, paths.Root);
        var tools = app.Services.GetRequiredService<IToolLocator>().Current;
        Assert.Equal(fake, tools.YtDlp);
        Assert.Equal(Path.Combine(root, "tools", "ffmpeg", "ffmpeg.exe"), tools.Ffmpeg);
        Assert.False(Directory.Exists(root), "Building the host touched the data folder.");
    }

    [Fact]
    public void Defaults_are_the_users_data_folder_and_the_ApiPort_setting()
    {
        var options = EngineHost.ReadOptions(EngineHost.CreateBuilder([]).Configuration);

        Assert.Null(options.Port);
        Assert.Equal(DataPaths.ForCurrentUser().Root, EngineHost.ResolveDataPaths(options).Root);
    }
}
