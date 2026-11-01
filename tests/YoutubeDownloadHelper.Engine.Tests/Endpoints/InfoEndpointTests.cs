using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Info;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Tools;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Tests.Endpoints;

public sealed class InfoEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private TestEngine _engine = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _engine = await TestEngine.StartAsync();

    public ValueTask DisposeAsync() => _engine.DisposeAsync();

    [Fact]
    public async Task Info_reports_versions_tools_auth_and_blocks()
    {
        using var client = _engine.CreateClient();

        var info = await client.GetFromJsonAsync<EngineInfo>(ApiRoutes.Info, Json, Ct);

        Assert.NotNull(info);
        Assert.Equal(ApiRoutes.Version, info.ApiVersion);
        Assert.Equal(EngineVersion.Current, info.EngineVersion);
        Assert.Equal(
            [new ToolInfo(ToolNames.YtDlp, null, false), new ToolInfo(ToolNames.Ffmpeg, null, false), new ToolInfo(ToolNames.Deno, null, false)],
            info.Tools);
        Assert.Equal(new AuthInfo(AuthStatus.NotLoggedIn), info.Auth);
        Assert.Empty(info.Blocks);
    }

    [Fact]
    public async Task Info_reports_installed_tools_and_stored_cookies()
    {
        foreach (var tool in (string[])["yt-dlp", "ffmpeg", "deno"])
        {
            Directory.CreateDirectory(Path.Combine(_engine.Paths.ToolsDir, tool));
            await File.WriteAllTextAsync(Path.Combine(_engine.Paths.ToolsDir, tool, tool + ".exe"), "", Ct);
        }

        _engine.App.Services.GetRequiredService<CookieJarStore>().Add(
            new CookieJar([new JarCookie(".youtube.com", "/", "SID", "x")]), CookieSource.Login);
        using var client = _engine.CreateClient();

        var info = await client.GetFromJsonAsync<EngineInfo>(ApiRoutes.Info, Json, Ct);

        Assert.All(info!.Tools, t => Assert.True(t.Installed, t.Tool));
        Assert.Equal(AuthStatus.Unknown, info.Auth.Status);
    }

    [Fact]
    public async Task Info_reports_a_paused_queue()
    {
        await _engine.App.Services.GetRequiredService<QueueActor>().PauseAllAsync(Ct);
        using var client = _engine.CreateClient();

        var info = await client.GetFromJsonAsync<EngineInfo>(ApiRoutes.Info, Json, Ct);

        Assert.Equal([QueueBlock.PausedByUser], info!.Blocks);
    }

    [Fact]
    public async Task Json_is_camelCase_with_enum_names()
    {
        await _engine.App.Services.GetRequiredService<QueueActor>().PauseAllAsync(Ct);
        using var client = _engine.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(ApiRoutes.Info, Ct));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("apiVersion").GetInt32());
        Assert.Equal("notLoggedIn", root.GetProperty("auth").GetProperty("status").GetString());
        Assert.Equal("pausedByUser", root.GetProperty("blocks")[0].GetString());
        Assert.Equal("yt-dlp", root.GetProperty("tools")[0].GetProperty("tool").GetString());
    }

    [Fact]
    public async Task Shutdown_answers_202_and_stops_the_host()
    {
        using var client = _engine.CreateClient();

        using var response = await client.PostAsync(ApiRoutes.EngineShutdown, null, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(_engine.App.Lifetime.ApplicationStopping.IsCancellationRequested);
    }
}
