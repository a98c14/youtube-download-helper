using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Api;
using YoutubeDownloadHelper.Contracts.Discovery;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Tests.Hosting;

public sealed class SingleInstanceTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void Second_acquire_of_the_same_name_fails_until_the_first_is_released()
    {
        var name = SingleInstance.MutexName(TestEngine.NewDataPaths());

        using (var first = SingleInstance.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstance.TryAcquire(name));
        }

        using var again = SingleInstance.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void Name_is_per_user_and_per_data_folder()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var root = Path.Combine(Path.GetTempPath(), "YoutubeDownloadHelper.Tests", "İndirgeç ılık");

        var name = SingleInstance.MutexName(new DataPaths(root));

        Assert.StartsWith($@"Global\{AppIdentity.InternalName}.Engine.{identity.User!.Value}.", name, StringComparison.Ordinal);
        Assert.True(name.All(char.IsAscii));
        Assert.Equal(name, SingleInstance.MutexName(new DataPaths(root.ToUpperInvariant())));
        Assert.Equal(name, SingleInstance.MutexName(new DataPaths(root + Path.DirectorySeparatorChar)));
        Assert.NotEqual(name, SingleInstance.MutexName(new DataPaths(root + "2")));
    }

    [Fact]
    public async Task Engine_exits_immediately_when_another_instance_holds_the_mutex()
    {
        var paths = TestEngine.NewDataPaths();
        using var holder = SingleInstance.TryAcquire(SingleInstance.MutexName(paths));
        Assert.NotNull(holder);

        using var engine = EngineProcess.Start(paths);

        Assert.Equal(EngineExitCodes.AlreadyRunning, await engine.WaitForExitAsync(ExitTimeout));
        Assert.False(Directory.Exists(paths.Root), "The second instance touched the data folder.");
    }

    [Fact]
    public async Task Second_engine_process_exits_and_the_first_keeps_running()
    {
        var paths = TestEngine.NewDataPaths();
        try
        {
            using var first = EngineProcess.Start(paths);
            var discovery = await first.WaitForDiscoveryAsync(paths);

            using (var second = EngineProcess.Start(paths))
            {
                Assert.Equal(EngineExitCodes.AlreadyRunning, await second.WaitForExitAsync(ExitTimeout));
            }

            Assert.False(first.Process.HasExited);
            Assert.Equal(discovery, DiscoveryFile.TryRead(paths.EngineJson));

            using var client = EngineProcess.CreateClient(discovery);
            using var response = await client.PostAsync(ApiRoutes.EngineShutdown, null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(EngineExitCodes.Success, await first.WaitForExitAsync(ExitTimeout));
            Assert.False(File.Exists(paths.EngineJson));
        }
        finally
        {
            EngineProcess.DeleteDataFolder(paths);
        }
    }

    [Fact]
    public async Task Real_engine_listens_on_loopback_and_falls_back_from_a_taken_port()
    {
        var paths = TestEngine.NewDataPaths();
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var takenPort = ((IPEndPoint)taken.LocalEndpoint).Port;
        try
        {
            using var engine = EngineProcess.Start(paths, takenPort);
            var discovery = await engine.WaitForDiscoveryAsync(paths);
            var baseUrl = new Uri(discovery.BaseUrl);

            Assert.Equal("127.0.0.1", baseUrl.Host);
            Assert.NotEqual(takenPort, baseUrl.Port);
            DiscoveryFileTests.AssertUserOnly(paths.EngineJson);

            using (var anonymous = EngineProcess.CreateClient(discovery, withToken: false))
            using (var unauthorized = await anonymous.GetAsync(ApiRoutes.Info, TestContext.Current.CancellationToken))
            {
                await RequestGuardTests.AssertErrorAsync(unauthorized, HttpStatusCode.Unauthorized, ApiErrorCodes.Unauthorized);
            }

            using var client = EngineProcess.CreateClient(discovery);
            using (var info = await client.GetAsync(ApiRoutes.Info, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, info.StatusCode);
                Assert.Empty(info.Headers.Server);
            }

            using (var shutdown = await client.PostAsync(ApiRoutes.EngineShutdown, null, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.Accepted, shutdown.StatusCode);
            }

            Assert.Equal(EngineExitCodes.Success, await engine.WaitForExitAsync(ExitTimeout));
            Assert.False(File.Exists(paths.EngineJson));
            Assert.Single(Directory.GetFiles(paths.LogsDir, "engine-*.log"));
        }
        finally
        {
            taken.Stop();
            EngineProcess.DeleteDataFolder(paths);
        }
    }
}
