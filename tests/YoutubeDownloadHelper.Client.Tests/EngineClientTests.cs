using System.Net;
using System.Text;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Api;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Discovery;
using YoutubeDownloadHelper.Contracts.Events;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Resolve;
using YoutubeDownloadHelper.Contracts.Settings;
using YoutubeDownloadHelper.Contracts.Tools;
using YoutubeDownloadHelper.Core.Tools;
using YoutubeDownloadHelper.Tests.Shared;
using static YoutubeDownloadHelper.Client.Tests.TestKit;

namespace YoutubeDownloadHelper.Client.Tests;

/// <summary>The typed methods against the real engine (PLAN.md §6.2).</summary>
public sealed class EngineClientTests
{
    private const long NextYear = 1_830_000_000;

    [Fact]
    public async Task Info_and_settings_round_trip()
    {
        await using var engine = await TestEngine.StartAsync();
        using var client = CreateClient(engine);

        var info = await client.GetInfoAsync(Ct);
        var settings = await client.GetSettingsAsync(Ct);
        var patched = await client.PatchSettingsAsync(new SettingsPatch(UiLanguage: UiLanguages.English, CloseToTray: false), Ct);

        Assert.Equal(ApiRoutes.Version, info.ApiVersion);
        Assert.Equal(AuthStatus.NotLoggedIn, info.Auth.Status);
        Assert.Equal(UiLanguages.Turkish, settings.UiLanguage);
        Assert.Equal(settings with { UiLanguage = UiLanguages.English, CloseToTray = false }, patched);
        Assert.Equal(patched, await client.GetSettingsAsync(Ct));
    }

    [Fact]
    public async Task Items_can_be_added_listed_controlled_and_removed()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = CreateClient(engine);

        var added = await client.AddItemsAsync(
            new AddItemsRequest(Urls: ["https://fake.test/success?id=client00001", "https://fake.test/success?id=client00002", "not a url"], Paused: true),
            Ct);
        Assert.Equal([AddItemOutcome.Added, AddItemOutcome.Added, AddItemOutcome.Invalid], added.Results.Select(r => r.Outcome));
        var (first, second) = (added.Results[0].ItemId!.Value, added.Results[1].ItemId!.Value);

        await client.MoveItemAsync(second, new MoveItemRequest(MoveTarget.Top), Ct);
        Assert.Equal([second, first], (await client.GetItemsAsync(cancellationToken: Ct)).Items.Select(i => i.Id));

        await client.ResumeItemAsync(first, Ct);
        await client.PauseItemAsync(first, Ct);
        var paused = await client.GetItemsAsync(states: [ItemState.Paused], cancellationToken: Ct);
        Assert.Equal([second, first], paused.Items.Select(i => i.Id));
        Assert.Equal(ItemState.Paused, (await client.GetItemAsync(first, Ct)).Item.State);

        var page = await client.GetItemsAsync(limit: 1, cancellationToken: Ct);
        Assert.Equal([second], page.Items.Select(i => i.Id));
        Assert.NotNull(page.NextCursor);
        var whole = await client.GetQueueSnapshotAsync(pageSize: 1, cancellationToken: Ct);
        Assert.Equal([second, first], whole.Items.Select(i => i.Id));
        Assert.Equal((page.Sequence, (string?)null), (whole.Sequence, whole.NextCursor));
        Assert.Equal([first], (await client.GetItemsAsync(search: "client00001", cancellationToken: Ct)).Items.Select(i => i.Id));

        await client.RemoveItemAsync(second, deleteFiles: true, Ct);
        Assert.Equal([first], (await client.GetItemsAsync(cancellationToken: Ct)).Items.Select(i => i.Id));
        Assert.Empty((await client.GetHistoryAsync(search: "client", cancellationToken: Ct)).Items);
    }

    [Fact]
    public async Task Queue_control_resolve_and_auth_work()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = CreateClient(engine);

        await client.PauseQueueAsync(Ct);
        Assert.Contains(QueueBlock.PausedByUser, (await client.GetInfoAsync(Ct)).Blocks);
        await client.ResumeQueueAsync(Ct);
        Assert.DoesNotContain(QueueBlock.PausedByUser, (await client.GetInfoAsync(Ct)).Blocks);

        var resolved = await client.ResolveAsync(new ResolveRequest("https://fake.test/playlist?count=3&title=Liste"), Ct);
        Assert.Equal((ResolveKind.Playlist, "Liste", 3), (resolved.Kind, resolved.Title, resolved.Entries.Count));

        var netscape = $".youtube.com\tTRUE\t/\tTRUE\t{NextYear}\tSID\tsid-value\nnot a cookie line\n";
        var stored = await client.SetCookiesAsync(new SetCookiesRequest(Netscape: netscape), Ct);
        Assert.Equal(1, stored.Stored);
        Assert.Equal([2], stored.SkippedLines);
        Assert.Equal(stored.Auth, await client.GetAuthAsync(Ct));
        await client.LogOutAsync(Ct);
        Assert.Equal(AuthStatus.NotLoggedIn, (await client.GetAuthAsync(Ct)).Status);
    }

    [Fact]
    public async Task Tools_can_be_listed_checked_and_rolled_back()
    {
        var feed = new FakeToolFeed().PublishAll("1.0");
        await using var engine = await TestEngine.StartAsync(toolFeed: feed);
        using var client = CreateClient(engine);
        await WaitForToolsAsync(client, t => t.Ready && t.Tools.All(d => d.LastCheck is not null));
        feed.Publish(ManagedTool.Deno, "1.1");

        await client.CheckToolsAsync(Ct);
        var updated = await WaitForToolsAsync(client, t => t.Tools[2].Version == "1.1");
        await client.RollbackToolAsync(ToolNames.Deno, Ct);
        var rolledBack = await client.GetToolsAsync(Ct);
        var noPrevious = await Assert.ThrowsAsync<EngineApiException>(() => client.RollbackToolAsync(ToolNames.Ffmpeg, Ct));

        Assert.Equal(("1.1", "1.0"), (updated.Tools[2].Version, updated.Tools[2].PreviousVersion));
        Assert.Equal(("1.0", "1.1"), (rolledBack.Tools[2].Version, rolledBack.Tools[2].PreviousVersion));
        Assert.Equal((HttpStatusCode.Conflict, ToolCodes.NoPreviousVersion), (noPrevious.StatusCode, noPrevious.Code));
    }

    [Fact]
    public async Task Errors_are_engine_messages()
    {
        await using var engine = await TestEngine.StartAsync();
        using var client = CreateClient(engine);
        using var stranger = new EngineClient(DiscoveryOf(engine) with { Token = "wrong" }, ClientOptions(engine.Server.CreateHandler()));

        var notFound = await Assert.ThrowsAsync<EngineApiException>(() => client.PauseItemAsync(12345, Ct));
        var invalid = await Assert.ThrowsAsync<EngineApiException>(() => client.GetItemsAsync(limit: 0, cancellationToken: Ct));
        var unprocessable = await Assert.ThrowsAsync<EngineApiException>(
            () => client.SetCookiesAsync(new SetCookiesRequest(Netscape: $".google.com\tTRUE\t/\tTRUE\t{NextYear}\tSID\tx\n"), Ct));
        var unauthorized = await Assert.ThrowsAsync<EngineApiException>(() => stranger.GetInfoAsync(Ct));

        Assert.Equal((HttpStatusCode.NotFound, QueueCodes.ItemNotFound), (notFound.StatusCode, notFound.Code));
        Assert.Equal((HttpStatusCode.BadRequest, ApiErrorCodes.InvalidParameter, "limit"), (invalid.StatusCode, invalid.Code, invalid.Error.Args["parameter"]));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, AuthCodes.NoYouTubeCookies), (unprocessable.StatusCode, unprocessable.Code));
        Assert.Equal((HttpStatusCode.Unauthorized, ApiErrorCodes.Unauthorized), (unauthorized.StatusCode, unauthorized.Code));
    }

    [Fact]
    public async Task An_error_without_an_engine_body_falls_back_to_the_status()
    {
        using var client = new EngineClient(
            new EngineDiscovery(ApiRoutes.Version, "http://127.0.0.1:1", "token", 1, "1.0.0"),
            ClientOptions(new CannedHandler(HttpStatusCode.BadGateway, "text/plain", "upstream down")));

        var error = await Assert.ThrowsAsync<EngineApiException>(() => client.GetInfoAsync(Ct));

        Assert.Equal((HttpStatusCode.BadGateway, ApiErrorCodes.HttpError, "502"), (error.StatusCode, error.Code, error.Error.Args["status"]));
    }

    [Fact]
    public async Task The_event_stream_parser_follows_the_SSE_format()
    {
        const string stream =
            ": a comment\n"
            + "id: 7\nevent: notify\ndata: {\"a\":\ndata: 1}\n\n"
            + "retry: 100\n\n"
            + "id:8\r\nevent:resync\r\ndata:{\"sequence\":8}\r\n\r\n";
        using var client = new EngineClient(
            new EngineDiscovery(ApiRoutes.Version, "http://127.0.0.1:1", "token", 1, "1.0.0"),
            ClientOptions(new CannedHandler(HttpStatusCode.OK, "text/event-stream", stream)));

        await using var connection = await client.OpenEventStreamAsync(lastEventId: 3, Ct);
        var first = await connection.ReadAsync(Ct);
        var second = await connection.ReadAsync(Ct);

        Assert.Equal((7L, EventTypes.Notify, "{\"a\":\n1}"), (first!.Id, first.Type, first.Data));
        Assert.Equal((8L, EventTypes.Resync), (second!.Id, second.Type));
        Assert.Equal<object?>(new ResyncData(8), second.GetData());
        Assert.Null(await connection.ReadAsync(Ct));
    }

    [Fact]
    public async Task Shutdown_is_accepted()
    {
        await using var engine = await TestEngine.StartAsync();
        using var client = CreateClient(engine);

        await client.ShutdownAsync(Ct);
    }

    private static async Task<ToolList> WaitForToolsAsync(EngineClient client, Func<ToolList, bool> match)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var tools = await client.GetToolsAsync(timeout.Token);
            if (match(tools))
            {
                return tools;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>Answers every request with the same response.</summary>
    private sealed class CannedHandler(HttpStatusCode status, string mediaType, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
    }
}
