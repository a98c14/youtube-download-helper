using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Tools;
using YoutubeDownloadHelper.Engine.Hosting;
using YoutubeDownloadHelper.Tests.Shared;

namespace YoutubeDownloadHelper.Engine.Tests;

/// <summary>
/// The real engine host on a <see cref="TestServer"/>, with a throwaway data folder whose path has Turkish dotted and
/// dotless I. Requests go to <c>http://127.0.0.1:47815</c>. The tool manager installs from a <see cref="FakeToolFeed"/>
/// (offline unless a test publishes releases) and smoke-tests with a <see cref="FakeSmokeTest"/>.
/// </summary>
public sealed class TestEngine : IAsyncDisposable
{
    public const int Port = 47815;

    private bool _stopped;

    private TestEngine(WebApplication app, DataPaths paths, FakeToolFeed toolFeed)
    {
        App = app;
        Paths = paths;
        ToolFeed = toolFeed;
    }

    public static Uri BaseAddress { get; } = new($"http://127.0.0.1:{Port}/");

    public WebApplication App { get; }

    public DataPaths Paths { get; }

    public FakeToolFeed ToolFeed { get; }

    public TestServer Server => App.GetTestServer();

    public ApiToken Token => App.Services.GetRequiredService<ApiToken>();

    public static DataPaths NewDataPaths() =>
        new(Path.Combine(Path.GetTempPath(), "YoutubeDownloadHelper.Tests", $"İndirgeç ılık {Guid.NewGuid():N}"));

    /// <param name="paths">The data folder; a new one by default.</param>
    /// <param name="mapExtra">Maps extra test endpoints after the engine's own.</param>
    /// <param name="toolFeed">The releases the tool manager sees; by default it is offline.</param>
    public static async Task<TestEngine> StartAsync(
        DataPaths? paths = null, Action<WebApplication>? mapExtra = null, FakeToolFeed? toolFeed = null)
    {
        paths ??= NewDataPaths();
        toolFeed ??= new FakeToolFeed { Offline = true };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer(o => o.BaseAddress = BaseAddress);
        builder.Configuration[$"{EngineOptions.Section}:{nameof(EngineOptions.Port)}"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        builder.Services.AddSingleton<IToolReleaseFeed>(toolFeed);
        builder.Services.AddSingleton<IToolSmokeTest>(new FakeSmokeTest());
        builder.AddEngine(paths);

        var app = builder.Build();
        app.MapEngine();
        mapExtra?.Invoke(app);
        var engine = new TestEngine(app, paths, toolFeed);
        try
        {
            await EngineHost.StartAsync(app, TestContext.Current.CancellationToken);
        }
        catch
        {
            await engine.DisposeAsync();
            throw;
        }

        return engine;
    }

    /// <summary>A client without a token.</summary>
    public HttpClient CreateAnonymousClient() => Server.CreateClient();

    /// <summary>A client that sends <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    public HttpClient CreateClient()
    {
        var client = Server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token.Value);
        return client;
    }

    public async Task StopAsync()
    {
        if (!_stopped)
        {
            _stopped = true;
            await App.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            await App.DisposeAsync();

            // Pooled connections keep the file open, which blocks deleting it on Windows.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Paths.Root))
            {
                Directory.Delete(Paths.Root, recursive: true);
            }
        }
    }
}
