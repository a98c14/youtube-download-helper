using YoutubeDownloadHelper.Core.Tests.FakeYtDlp;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Tests.Tools;

public sealed class ProcessToolSmokeTestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ProcessToolSmokeTest _smoke = new(TimeProvider.System);

    [Fact]
    public async Task Passes_a_binary_that_prints_its_version()
    {
        var result = await _smoke.RunAsync(FakeYtDlpProcess.ExePath, ["--version"], Ct);

        Assert.True(result.Passed, result.Output);
        Assert.NotEmpty(result.Output);
    }

    [Fact]
    public async Task Fails_a_binary_that_exits_with_an_error()
    {
        var result = await _smoke.RunAsync(FakeYtDlpProcess.ExePath, ["--no-such-option-at-all"], Ct);

        Assert.False(result.Passed);
    }

    [Fact]
    public async Task Fails_a_missing_or_corrupt_binary()
    {
        using var folder = new FakeYtDlpFolder();
        var corrupt = folder.In("corrupt.exe");
        await File.WriteAllTextAsync(corrupt, "not a program", Ct);

        Assert.False((await _smoke.RunAsync(folder.In("missing.exe"), ["--version"], Ct)).Passed);
        Assert.False((await _smoke.RunAsync(corrupt, ["--version"], Ct)).Passed);
    }
}
