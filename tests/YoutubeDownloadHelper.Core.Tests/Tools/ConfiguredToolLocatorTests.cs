using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Tests.Tools;

public sealed class ConfiguredToolLocatorTests
{
    private static readonly DataPaths Paths = new(@"C:\Data İı");

    [Fact]
    public void Defaults_to_the_tools_folder()
    {
        var tools = new ConfiguredToolLocator(Paths, new ToolLocatorOptions()).Current;

        Assert.Equal(new ToolPaths(@"C:\Data İı\tools\yt-dlp\yt-dlp.exe", @"C:\Data İı\tools\ffmpeg\ffmpeg.exe", @"C:\Data İı\tools\deno\deno.exe"), tools);
    }

    [Fact]
    public void Configured_paths_override_each_default()
    {
        var tools = new ConfiguredToolLocator(Paths, new ToolLocatorOptions { YtDlp = @"D:\fake\YoutubeDownloadHelper.FakeYtDlp.exe", Deno = " " }).Current;

        Assert.Equal(@"D:\fake\YoutubeDownloadHelper.FakeYtDlp.exe", tools.YtDlp);
        Assert.Equal(@"C:\Data İı\tools\ffmpeg\ffmpeg.exe", tools.Ffmpeg);
        Assert.Equal(@"C:\Data İı\tools\deno\deno.exe", tools.Deno);
    }

    [Fact]
    public void AddTools_keeps_a_locator_that_is_already_registered()
    {
        var replacement = new ConfiguredToolLocator(Paths, new ToolLocatorOptions { YtDlp = @"D:\other.exe" });
        using var provider = new ServiceCollection()
            .AddSingleton(Paths)
            .AddSingleton<IToolLocator>(replacement)
            .AddTools()
            .BuildServiceProvider();

        Assert.Same(replacement, provider.GetRequiredService<IToolLocator>());
    }
}
