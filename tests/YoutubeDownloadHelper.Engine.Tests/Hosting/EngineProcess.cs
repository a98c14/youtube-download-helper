using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Discovery;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Tests.Hosting;

/// <summary>The real <c>YoutubeDownloadHelper.Engine.exe</c> (copied next to the tests), run against a test data folder.</summary>
public sealed class EngineProcess : IDisposable
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private EngineProcess(Process process) => Process = process;

    public Process Process { get; }

    public static EngineProcess Start(DataPaths paths, int port = 0)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, AppIdentity.EngineExecutableName))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        info.ArgumentList.Add($"--{EngineOptions.Section}:{nameof(EngineOptions.DataRoot)}={paths.Root}");
        info.ArgumentList.Add($"--{EngineOptions.Section}:{nameof(EngineOptions.Port)}={port}");

        // Overridden tools are not managed, so the tool manager never goes to GitHub (no network in tests).
        foreach (var tool in (string[])["YtDlp", "Ffmpeg", "Deno"])
        {
            info.ArgumentList.Add($"--{EngineOptions.ToolsSection}:{tool}={Path.Combine(paths.Root, "no-tools", tool + ".exe")}");
        }
        return new EngineProcess(Process.Start(info) ?? throw new InvalidOperationException("The engine did not start."));
    }

    /// <summary>Waits until the engine has written its discovery file.</summary>
    public async Task<EngineDiscovery> WaitForDiscoveryAsync(DataPaths paths)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartTimeout)
        {
            Assert.False(Process.HasExited, $"The engine exited with code {(Process.HasExited ? Process.ExitCode : 0)}.");
            if (DiscoveryFile.TryRead(paths.EngineJson) is { } discovery && discovery.Pid == Process.Id)
            {
                return discovery;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The engine did not write its discovery file.");
    }

    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        await Process.WaitForExitAsync(cts.Token);
        return Process.ExitCode;
    }

    public static HttpClient CreateClient(EngineDiscovery discovery, bool withToken = true)
    {
        var client = new HttpClient { BaseAddress = new Uri(discovery.BaseUrl) };
        if (withToken)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", discovery.Token);
        }

        return client;
    }

    public void Dispose()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
            Process.WaitForExit();
        }

        Process.Dispose();
    }

    public static void DeleteDataFolder(DataPaths paths)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(paths.Root))
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }
}
