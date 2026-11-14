using System.Diagnostics;
using System.Text.Json;
using YoutubeDownloadHelper.Client.Discovery;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Discovery;
using YoutubeDownloadHelper.Tests.Shared;
using static YoutubeDownloadHelper.Client.Tests.TestKit;

namespace YoutubeDownloadHelper.Client.Tests;

/// <summary>Finding the engine from <c>engine.json</c>, and telling a stale file from a live engine (PLAN.md §6.1).</summary>
public sealed class DiscoveryTests
{
    [Fact]
    public async Task The_running_engines_discovery_file_is_ready()
    {
        await using var engine = await TestEngine.StartAsync();

        var check = await EngineDiscoveryFile.CheckAsync(engine.Paths.EngineJson, ClientOptions(engine.Server.CreateHandler()), cancellationToken: Ct);

        Assert.Equal(DiscoveryStatus.Ready, check.Status);
        Assert.True(check.IsReady);
        Assert.Equal((Environment.ProcessId, engine.Token.Value, "http://127.0.0.1:47815"), (check.Discovery!.Pid, check.Discovery.Token, check.Discovery.BaseUrl));
        Assert.Equal(ApiRoutes.Version, check.Info!.ApiVersion);
        Assert.Equal(check.Discovery, await new DiscoveryFileLocator(engine.Paths.EngineJson).LocateAsync(Ct));
    }

    [Fact]
    public async Task A_file_naming_a_process_that_is_gone_is_stale()
    {
        await using var engine = await TestEngine.StartAsync();
        var path = Path.Combine(engine.Paths.Root, "stale.json");
        Write(path, DiscoveryOf(engine) with { Pid = ExitedProcessId() });

        var check = await EngineDiscoveryFile.CheckAsync(path, ClientOptions(engine.Server.CreateHandler()), cancellationToken: Ct);

        Assert.Equal(DiscoveryStatus.ProcessExited, check.Status);
        Assert.Null(await new DiscoveryFileLocator(path).LocateAsync(Ct));
    }

    [Fact]
    public async Task A_file_whose_engine_does_not_answer_is_stale()
    {
        await using var engine = await TestEngine.StartAsync();
        var path = Path.Combine(engine.Paths.Root, "other-token.json");

        // A live PID (this process) but another engine's token: what a reused PID or a hung engine looks like.
        Write(path, DiscoveryOf(engine) with { Token = "an-old-engines-token" });

        var check = await EngineDiscoveryFile.CheckAsync(path, ClientOptions(engine.Server.CreateHandler()), cancellationToken: Ct);

        Assert.Equal(DiscoveryStatus.Unresponsive, check.Status);
        Assert.IsType<EngineApiException>(check.Error);
    }

    [Fact]
    public async Task A_file_whose_engine_cannot_be_reached_is_stale()
    {
        var path = TempFile();
        Write(path, new EngineDiscovery(ApiRoutes.Version, "http://127.0.0.1:47815", "token", Environment.ProcessId, "1.0.0"));
        try
        {
            var check = await EngineDiscoveryFile.CheckAsync(path, ClientOptions(new RefusingHandler()), cancellationToken: Ct);

            Assert.Equal(DiscoveryStatus.Unresponsive, check.Status);
            Assert.IsType<HttpRequestException>(check.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Missing_unreadable_and_incompatible_files_are_reported()
    {
        var missing = TempFile();
        var garbage = TempFile();
        var future = TempFile();
        await File.WriteAllTextAsync(garbage, "{ not json", Ct);
        Write(future, new EngineDiscovery(ApiRoutes.Version + 1, "http://127.0.0.1:47815", "token", Environment.ProcessId, "9.0.0"));
        try
        {
            Assert.Equal(DiscoveryStatus.Missing, (await EngineDiscoveryFile.CheckAsync(missing, cancellationToken: Ct)).Status);
            Assert.Equal(DiscoveryStatus.Unreadable, (await EngineDiscoveryFile.CheckAsync(garbage, cancellationToken: Ct)).Status);
            Assert.Equal(DiscoveryStatus.Incompatible, (await EngineDiscoveryFile.CheckAsync(future, cancellationToken: Ct)).Status);
            Assert.Null(await new DiscoveryFileLocator(missing).LocateAsync(Ct));
            Assert.Null(await new DiscoveryFileLocator(future).LocateAsync(Ct));
        }
        finally
        {
            File.Delete(garbage);
            File.Delete(future);
        }
    }

    [Fact]
    public void The_default_path_is_in_the_internal_data_folder()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.DataFolderName,
            EngineDiscovery.FileName);

        Assert.Equal(expected, EngineDiscoveryFile.DefaultPath);
        Assert.Equal(expected, new DiscoveryFileLocator().Path);
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"YoutubeDownloadHelper.Client.Tests {Guid.NewGuid():N}.json");

    private static void Write(string path, EngineDiscovery discovery) =>
        File.WriteAllText(path, JsonSerializer.Serialize(discovery, EngineJson.Options));

    /// <summary>The ID of a process that has exited (the fake yt-dlp, run once).</summary>
    private static int ExitedProcessId()
    {
        using var process = Process.Start(new ProcessStartInfo(TestEngine.FakeYtDlpExe, "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.Id;
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused.");
    }
}
