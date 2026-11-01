using System.Diagnostics;
using System.Globalization;
using System.Text;
using YoutubeDownloadHelper.Core.Runner;
using YoutubeDownloadHelper.Core.Tests.FakeYtDlp;

namespace YoutubeDownloadHelper.Core.Tests.Runner;

public sealed class JobObjectProcessTests : IDisposable
{
    private readonly FakeYtDlpFolder _folder = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Kill_ends_the_process_and_its_children()
    {
        using var process = StartSpawnChild();
        using var child = await ChildOfAsync(process);

        process.Kill();

        Assert.Equal(1, await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.True(child.WaitForExit(10_000));
    }

    [Fact]
    public async Task Disposing_ends_the_process_tree()
    {
        var process = StartSpawnChild();
        using var child = await ChildOfAsync(process);

        process.Dispose();

        Assert.True(child.WaitForExit(10_000));
    }

    [Fact]
    public async Task The_child_gets_exactly_the_given_environment_and_arguments()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
            ["YDH_TEST_VALUE"] = "kept",
        };

        using var process = JobObjectProcess.Start(cmd, ["/d", "/c", "set"], environment);
        var output = await ReadAllAsync(process.StandardOutput);

        Assert.Equal(0, await process.WaitForExitAsync(Token));
        Assert.Contains("YDH_TEST_VALUE=kept", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PATH=", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_exit_code_and_both_streams_come_through()
    {
        using var process = JobObjectProcess.Start(FakeYtDlpProcess.ExePath, []);
        var stdout = ReadAllAsync(process.StandardOutput);
        var stderr = ReadAllAsync(process.StandardError);

        Assert.Equal(2, await process.WaitForExitAsync(Token));
        Assert.Equal("", await stdout);
        Assert.Contains("You must provide at least one URL.", await stderr, StringComparison.Ordinal);
    }

    private JobObjectProcess StartSpawnChild() =>
        JobObjectProcess.Start(FakeYtDlpProcess.ExePath, [.. FakeYtDlpProcess.EngineArgs(_folder.Temp, _folder.Home), "--", "https://fake.test/spawn-child"]);

    private static async Task<Process> ChildOfAsync(JobObjectProcess process)
    {
        using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8, false, 4096, leaveOpen: true);
        while (await reader.ReadLineAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Token) is { } line)
        {
            if (line.StartsWith("[fake] child pid ", StringComparison.Ordinal))
            {
                var child = Process.GetProcessById(int.Parse(line["[fake] child pid ".Length..], CultureInfo.InvariantCulture));
                Assert.False(child.HasExited);
                return child;
            }
        }

        throw new InvalidOperationException("The fake closed stdout before starting its child.");
    }

    private static Task<string> ReadAllAsync(Stream stream) =>
        new StreamReader(stream, Encoding.UTF8).ReadToEndAsync(Token);
}
