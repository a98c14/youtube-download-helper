using System.Net;
using System.Net.Http.Json;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Api;
using YoutubeDownloadHelper.Contracts.Info;
using YoutubeDownloadHelper.Contracts.Presets;
using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Engine.Tests.Endpoints;

/// <summary>
/// The item and queue endpoints. The tool feed is offline, so the tools are missing and nothing starts: items stay
/// where the commands put them.
/// </summary>
public sealed class ItemsEndpointTests : IAsyncLifetime
{
    private const string VideoId = "dQw4w9WgXcQ";

    private TestEngine _engine = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _engine = await TestEngine.StartAsync();
        _client = _engine.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _engine.DisposeAsync();
    }

    [Fact]
    public async Task Add_reports_added_duplicate_and_invalid_for_each_input()
    {
        var results = await AddAsync(new AddItemsRequest(
            Urls: [$"https://www.youtube.com/watch?v={VideoId}&t=10", $"https://youtu.be/{VideoId}", "not a url", "ftp://fake.test/x", " https://fake.test/success?id=fakefake001 "],
            VideoIds: [VideoId, "too-short"]));

        Assert.Equal(
            [
                (AddItemOutcome.Added, (string?)null),
                (AddItemOutcome.Duplicate, ItemCodes.DuplicateInRequest),
                (AddItemOutcome.Invalid, ItemCodes.InvalidUrl),
                (AddItemOutcome.Invalid, ItemCodes.InvalidUrl),
                (AddItemOutcome.Added, null),
                (AddItemOutcome.Duplicate, ItemCodes.DuplicateInRequest),
                (AddItemOutcome.Invalid, ItemCodes.InvalidVideoId),
            ],
            results.Select(r => (r.Outcome, r.Message?.Code)));
        Assert.Equal("not a url", results[2].Message!.Args["input"]);

        var list = await _client.GetFromJsonAsync<ItemList>(ApiRoutes.Items, TestJson.Options, Ct);
        Assert.Equal([results[0].ItemId!.Value, results[4].ItemId!.Value], list!.Items.Select(i => i.Id));
        var youTube = list.Items[0];
        Assert.Equal(("youtube", VideoId, PresetIds.Best, ItemState.Queued, 1L), (youTube.Extractor, youTube.VideoId, youTube.PresetId, youTube.State, youTube.Position));
        Assert.Equal("https://fake.test/success?id=fakefake001", list.Items[1].Url);
    }

    [Fact]
    public async Task A_queued_video_is_a_duplicate_unless_forced()
    {
        var first = (await AddAsync(new AddItemsRequest(VideoIds: [VideoId])))[0];

        var again = (await AddAsync(new AddItemsRequest(Urls: [$"https://www.youtube.com/shorts/{VideoId}"])))[0];
        var sameUrl = (await AddAsync(new AddItemsRequest(Urls: [$"https://www.youtube.com/watch?v={VideoId}"])))[0];
        var forced = (await AddAsync(new AddItemsRequest(VideoIds: [VideoId], Force: true)))[0];

        Assert.Equal((AddItemOutcome.Duplicate, first.ItemId, ItemCodes.AlreadyQueued), (again.Outcome, again.ItemId, again.Message?.Code));
        Assert.Equal(AddItemOutcome.Duplicate, sameUrl.Outcome);
        Assert.Equal(AddItemOutcome.Added, forced.Outcome);
        Assert.NotEqual(first.ItemId, forced.ItemId);
    }

    [Fact]
    public async Task Add_takes_a_preset_a_folder_and_paused()
    {
        var added = (await AddAsync(new AddItemsRequest(Urls: ["https://fake.test/success"], PresetId: PresetIds.AudioMp3, Folder: @"C:\Müzik", Paused: true)))[0];

        var item = (await GetDetailAsync(added.ItemId!.Value)).Item;
        Assert.Equal((PresetIds.AudioMp3, @"C:\Müzik", ItemState.Paused), (item.PresetId, item.Folder, item.State));
    }

    [Fact]
    public async Task Add_rejects_a_request_without_inputs_or_with_an_unknown_preset()
    {
        using var empty = await _client.PostAsJsonAsync(ApiRoutes.Items, new AddItemsRequest(), TestJson.Options, Ct);
        using var preset = await _client.PostAsJsonAsync(ApiRoutes.Items, new AddItemsRequest(Urls: ["https://fake.test/success"], PresetId: "8k"), TestJson.Options, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, preset.StatusCode);
        var error = await TestJson.ReadErrorAsync(preset);
        Assert.Equal((ApiErrorCodes.InvalidParameter, "presetId"), (error.Code, error.Args["parameter"]));
        Assert.Empty((await _client.GetFromJsonAsync<ItemList>(ApiRoutes.Items, TestJson.Options, Ct))!.Items);
    }

    [Fact]
    public async Task List_filters_by_state_and_search_and_pages_with_a_cursor()
    {
        var ids = (await AddAsync(new AddItemsRequest(Urls:
            ["https://fake.test/success?id=list0000001", "https://fake.test/success?id=list0000002", "https://fake.test/success?id=LIST0000003"]))).Select(r => r.ItemId!.Value).ToList();
        await PostAsync(ApiRoutes.ItemPath(ApiRoutes.ItemPause, ids[1]), HttpStatusCode.NoContent);

        var paused = await ListAsync("?state=paused");
        var queuedOrPaused = await ListAsync("?state=Queued,%20paused");
        var search = await ListAsync("?search=list000000");
        var page1 = await ListAsync("?limit=2");
        var page2 = await ListAsync($"?limit=2&cursor={page1.NextCursor}");

        Assert.Equal([ids[1]], paused.Items.Select(i => i.Id));
        Assert.Equal(ids, queuedOrPaused.Items.Select(i => i.Id));
        Assert.Equal(ids, search.Items.Select(i => i.Id));
        Assert.Equal(ids[..2], page1.Items.Select(i => i.Id));
        Assert.NotNull(page1.NextCursor);
        Assert.Equal([ids[2]], page2.Items.Select(i => i.Id));
        Assert.Null(page2.NextCursor);
        Assert.True(page1.Sequence > 0);

        foreach (var bad in (string[])["?state=sleeping", "?cursor=abc", "?limit=0"])
        {
            using var response = await _client.GetAsync(ApiRoutes.Items + bad, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ApiErrorCodes.InvalidParameter, (await TestJson.ReadErrorAsync(response)).Code);
        }
    }

    [Fact]
    public async Task Pause_resume_and_retry_answer_204_404_or_409()
    {
        var id = (await AddAsync(new AddItemsRequest(Urls: ["https://fake.test/success"])))[0].ItemId!.Value;

        await PostAsync(ApiRoutes.ItemPath(ApiRoutes.ItemPause, id), HttpStatusCode.NoContent);
        Assert.Equal(ItemState.Paused, (await GetDetailAsync(id)).Item.State);
        var again = await PostAsync(ApiRoutes.ItemPath(ApiRoutes.ItemPause, id), HttpStatusCode.Conflict);
        await PostAsync(ApiRoutes.ItemPath(ApiRoutes.ItemResume, id), HttpStatusCode.NoContent);
        var retry = await PostAsync(ApiRoutes.ItemPath(ApiRoutes.ItemRetry, id), HttpStatusCode.Conflict);
        var missing = await PostAsync(ApiRoutes.ItemPath(ApiRoutes.ItemPause, 9999), HttpStatusCode.NotFound);
        await PostAsync(ApiRoutes.Items + "/abc/pause", HttpStatusCode.BadRequest);

        Assert.Equal(QueueCodes.InvalidTransition, again!.Code);
        Assert.Equal(QueueCodes.InvalidTransition, retry!.Code);
        Assert.Equal((QueueCodes.ItemNotFound, "9999"), (missing!.Code, missing.Args["itemId"]));
        Assert.Equal(ItemState.Queued, (await GetDetailAsync(id)).Item.State);
    }

    [Fact]
    public async Task Move_reorders_the_queue()
    {
        var ids = (await AddAsync(new AddItemsRequest(Urls: ["https://fake.test/success?id=move0000001", "https://fake.test/success?id=move0000002", "https://fake.test/success?id=move0000003"])))
            .Select(r => r.ItemId!.Value).ToList();

        await MoveAsync(ids[2], new MoveItemRequest(MoveTarget.Top), HttpStatusCode.NoContent);
        Assert.Equal([ids[2], ids[0], ids[1]], (await ListAsync(string.Empty)).Items.Select(i => i.Id));
        await MoveAsync(ids[2], new MoveItemRequest(MoveTarget.Bottom), HttpStatusCode.NoContent);
        await MoveAsync(ids[1], new MoveItemRequest(MoveTarget.Before, ids[0]), HttpStatusCode.NoContent);
        var list = await ListAsync(string.Empty);
        var self = await MoveAsync(ids[1], new MoveItemRequest(MoveTarget.Before, ids[1]), HttpStatusCode.Conflict);
        var missing = await MoveAsync(ids[1], new MoveItemRequest(MoveTarget.Before, 9999), HttpStatusCode.NotFound);

        Assert.Equal([ids[1], ids[0], ids[2]], list.Items.Select(i => i.Id));
        Assert.Equal([1L, 2, 3], list.Items.Select(i => i.Position));
        Assert.Equal(QueueCodes.InvalidMove, self!.Code);
        Assert.Equal(QueueCodes.ItemNotFound, missing!.Code);
    }

    [Fact]
    public async Task Delete_removes_the_item()
    {
        var id = (await AddAsync(new AddItemsRequest(Urls: ["https://fake.test/success"])))[0].ItemId!.Value;

        using var removed = await _client.DeleteAsync(ApiRoutes.ItemPath(ApiRoutes.Item, id) + "?deleteFiles=true", Ct);
        using var again = await _client.DeleteAsync(ApiRoutes.ItemPath(ApiRoutes.Item, id), Ct);
        using var detail = await _client.GetAsync(ApiRoutes.ItemPath(ApiRoutes.Item, id), Ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(QueueCodes.ItemNotFound, (await TestJson.ReadErrorAsync(detail)).Code);
        Assert.Empty((await ListAsync(string.Empty)).Items);
    }

    [Fact]
    public async Task Detail_has_the_transitions()
    {
        var id = (await AddAsync(new AddItemsRequest(Urls: ["https://fake.test/success"])))[0].ItemId!.Value;
        await PostAsync(ApiRoutes.ItemPath(ApiRoutes.ItemPause, id), HttpStatusCode.NoContent);

        var detail = await GetDetailAsync(id);

        Assert.Equal(
            [((ItemState?)null, ItemState.Added, "added"), (ItemState.Added, ItemState.Queued, "added"), (ItemState.Queued, ItemState.Paused, "user.pause")],
            detail.Events.Select(e => (e.FromState, e.ToState, e.Reason)));
        Assert.Empty(detail.Attempts);
        Assert.Empty(detail.OutputTail);
    }

    [Fact]
    public async Task Queue_pause_and_resume_raise_and_lift_the_block()
    {
        await PostAsync(ApiRoutes.QueuePause, HttpStatusCode.NoContent);
        var paused = await _client.GetFromJsonAsync<EngineInfo>(ApiRoutes.Info, TestJson.Options, Ct);
        await PostAsync(ApiRoutes.QueueResume, HttpStatusCode.NoContent);
        var resumed = await _client.GetFromJsonAsync<EngineInfo>(ApiRoutes.Info, TestJson.Options, Ct);

        Assert.Contains(QueueBlock.PausedByUser, paused!.Blocks);
        Assert.DoesNotContain(QueueBlock.PausedByUser, resumed!.Blocks);
    }

    [Fact]
    public async Task History_rejects_a_bad_cursor()
    {
        using var response = await _client.GetAsync(ApiRoutes.History + "?cursor=yesterday", Ct);
        var empty = await _client.GetFromJsonAsync<HistoryList>(ApiRoutes.History, TestJson.Options, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(empty!.Items);
        Assert.Null(empty.NextCursor);
    }

    private async Task<IReadOnlyList<AddItemResult>> AddAsync(AddItemsRequest request)
    {
        using var response = await _client.PostAsJsonAsync(ApiRoutes.Items, request, TestJson.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await TestJson.ReadAsync<AddItemsResponse>(response)).Results;
    }

    private async Task<ItemList> ListAsync(string query) =>
        (await _client.GetFromJsonAsync<ItemList>(ApiRoutes.Items + query, TestJson.Options, Ct))!;

    private async Task<ItemDetail> GetDetailAsync(long id) =>
        (await _client.GetFromJsonAsync<ItemDetail>(ApiRoutes.ItemPath(ApiRoutes.Item, id), TestJson.Options, Ct))!;

    private async Task<Contracts.Messages.EngineMessage?> PostAsync(string path, HttpStatusCode expected)
    {
        using var response = await _client.PostAsync(path, null, Ct);
        Assert.Equal(expected, response.StatusCode);
        return response.IsSuccessStatusCode ? null : await TestJson.ReadErrorAsync(response);
    }

    private async Task<Contracts.Messages.EngineMessage?> MoveAsync(long id, MoveItemRequest request, HttpStatusCode expected)
    {
        using var response = await _client.PostAsJsonAsync(ApiRoutes.ItemPath(ApiRoutes.ItemMove, id), request, TestJson.Options, Ct);
        Assert.Equal(expected, response.StatusCode);
        return response.IsSuccessStatusCode ? null : await TestJson.ReadErrorAsync(response);
    }
}
