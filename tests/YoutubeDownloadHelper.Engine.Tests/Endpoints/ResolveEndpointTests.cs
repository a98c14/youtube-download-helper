using System.Net;
using System.Net.Http.Json;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Api;
using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Resolve;

namespace YoutubeDownloadHelper.Engine.Tests.Endpoints;

/// <summary>
/// <c>POST /resolve</c> and <c>POST /items</c> with <c>expandPlaylists</c> and picker selections, against FakeYtDlp
/// (<see cref="TestEngine.StartAsync"/> with <c>fakeTools</c>). Items are added paused unless a test downloads them.
/// </summary>
public sealed class ResolveEndpointTests : IAsyncLifetime
{
    private TestEngine _engine = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _engine = await TestEngine.StartAsync(fakeTools: true);
        _client = _engine.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _engine.DisposeAsync();
    }

    [Fact]
    public async Task A_video_resolves_with_its_metadata()
    {
        var resolved = await ResolveAsync("https://fake.test/success?id=fkresolve01&title=Ak%C4%B1%C5%9F&size=1M");

        Assert.Equal((ResolveKind.Video, "fkresolve01", "Akış", "Fake Channel", 1, false), (resolved.Kind, resolved.Id, resolved.Title, resolved.Channel, resolved.TotalCount, resolved.Truncated));
        var entry = Assert.Single(resolved.Entries);
        Assert.Equal(
            (ResolvedEntryKind.Video, "https://fake.test/success?id=fkresolve01&title=Ak%C4%B1%C5%9F&size=1M", "youtube", "fkresolve01", "Akış", "public", "not_live", 1L << 20, (EntryDuplicate?)null),
            (entry.Kind, entry.Url, entry.Extractor, entry.VideoId, entry.Title, entry.Availability, entry.LiveStatus, entry.SizeBytes, entry.Duplicate));
        Assert.Equal(new DateOnly(2026, 1, 2), entry.UploadDate);
    }

    [Fact]
    public async Task A_YouTube_video_URL_in_a_playlist_resolves_as_the_video()
    {
        var resolved = await ResolveAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLfakeplaylist");

        Assert.Equal(ResolveKind.Video, resolved.Kind);
        Assert.Equal("dQw4w9WgXcQ", Assert.Single(resolved.Entries).VideoId);
    }

    [Fact]
    public async Task A_playlist_resolves_to_flat_entries()
    {
        var resolved = await ResolveAsync("https://fake.test/playlist?count=3&title=Liste");

        Assert.Equal((ResolveKind.Playlist, "PLfakeplaylist", "Liste", 3, false), (resolved.Kind, resolved.Id, resolved.Title, resolved.TotalCount, resolved.Truncated));
        Assert.Equal(["fkp00000003", "fkp00000002", "fkp00000001"], resolved.Entries.Select(e => e.VideoId));
        var entry = resolved.Entries[0];
        Assert.Equal(
            (ResolvedEntryKind.Video, "https://fake.test/success?id=fkp00000003", "Fake entry 3", 300.0, (EntryDuplicate?)null),
            (entry.Kind, entry.Url, entry.Title, entry.DurationSeconds, entry.Duplicate));
    }

    [Fact]
    public async Task A_listing_longer_than_the_limit_is_truncated()
    {
        var resolved = await ResolveAsync("https://fake.test/playlist?count=5", limit: 2);

        Assert.True(resolved.Truncated);
        Assert.Equal((5, 2), (resolved.TotalCount, resolved.Entries.Count));
    }

    [Fact]
    public async Task Channel_tabs_resolve_with_their_badges()
    {
        var videos = await ResolveAsync("https://fake.test/channel/videos?count=2&channel=Kanal");
        var shorts = await ResolveAsync("https://fake.test/channel/shorts?count=2");
        var streams = await ResolveAsync("https://fake.test/channel/streams?count=2");

        Assert.Equal((ResolveKind.Playlist, "Kanal", "Kanal - Videos", (int?)null), (videos.Kind, videos.Channel, videos.Title, videos.TotalCount));
        Assert.Equal(["fkv00000002", "fkv00000001"], videos.Entries.Select(e => e.VideoId));
        Assert.All(shorts.Entries, e => Assert.Equal(30.0, e.DurationSeconds));
        Assert.All(streams.Entries, e => Assert.Equal("was_live", e.LiveStatus));
    }

    [Fact]
    public async Task A_channel_without_a_tab_lists_its_tabs()
    {
        var resolved = await ResolveAsync("https://fake.test/channel/tabs?count=2");

        Assert.Equal(ResolveKind.Playlist, resolved.Kind);
        Assert.All(resolved.Entries, e => Assert.Equal((ResolvedEntryKind.Playlist, (string?)null), (e.Kind, e.VideoId)));
        Assert.Equal(
            ["https://fake.test/channel/videos?count=2", "https://fake.test/channel/shorts?count=2", "https://fake.test/channel/streams?count=2"],
            resolved.Entries.Select(e => e.Url));
    }

    [Fact]
    public async Task The_members_tab_needs_login()
    {
        var error = await ResolveErrorAsync(new ResolveRequest("https://fake.test/channel/members"), HttpStatusCode.UnprocessableEntity);

        Assert.Equal(ErrorCodes.PlaylistNotFound, error.Code);
    }

    [Fact]
    public async Task A_yt_dlp_failure_answers_422_with_the_classified_error()
    {
        var error = await ResolveErrorAsync(new ResolveRequest("https://fake.test/members-only"), HttpStatusCode.UnprocessableEntity);

        Assert.Equal(ErrorCodes.MembersOnly, error.Code);
    }

    [Fact]
    public async Task Bad_input_answers_400()
    {
        var url = await ResolveErrorAsync(new ResolveRequest("not a url"), HttpStatusCode.BadRequest);
        var limit = await ResolveErrorAsync(new ResolveRequest("https://fake.test/playlist", Limit: ResolveLimits.MaxEntries + 1), HttpStatusCode.BadRequest);

        Assert.Equal((ApiErrorCodes.InvalidParameter, "url"), (url.Code, url.Args["parameter"]));
        Assert.Equal((ApiErrorCodes.InvalidParameter, "limit"), (limit.Code, limit.Args["parameter"]));
    }

    [Fact]
    public async Task Entries_are_flagged_when_queued_or_downloaded()
    {
        var downloaded = (await AddAsync(new AddItemsRequest(Urls: ["https://fake.test/success?id=fkv00000003"])))[0].ItemId!.Value;
        await WaitForStateAsync(downloaded, ItemState.Completed);
        var queued = (await AddAsync(new AddItemsRequest(VideoIds: ["fkv00000002"], Paused: true)))[0].ItemId!.Value;

        var resolved = await ResolveAsync("https://fake.test/channel/videos?count=3");

        Assert.Equal(
            [(EntryDuplicate.Downloaded, downloaded), (EntryDuplicate.Queued, queued), ((EntryDuplicate?)null, (long?)null)],
            resolved.Entries.Select(e => (e.Duplicate, e.ItemId)));

        var single = await ResolveAsync("https://www.youtube.com/watch?v=fkv00000002");
        Assert.Equal((EntryDuplicate.Queued, queued), (single.Entries[0].Duplicate, single.Entries[0].ItemId));
    }

    [Fact]
    public async Task Expanding_a_playlist_adds_each_video()
    {
        const string playlist = "https://fake.test/playlist?count=3";
        var results = await AddAsync(new AddItemsRequest(Urls: [playlist], Paused: true, ExpandPlaylists: PlaylistExpansion.All));

        Assert.Equal(
            ["https://fake.test/success?id=fkp00000003", "https://fake.test/success?id=fkp00000002", "https://fake.test/success?id=fkp00000001"],
            results.Select(r => r.Input));
        Assert.All(results, r => Assert.Equal((AddItemOutcome.Added, playlist), (r.Outcome, r.Playlist)));

        var items = (await ListAsync()).Items;
        Assert.Equal(["fkp00000003", "fkp00000002", "fkp00000001"], items.Select(i => i.VideoId));
        Assert.Equal(("youtube", "Fake entry 3", 300.0, ItemState.Paused), (items[0].Extractor, items[0].Title, items[0].DurationSeconds, items[0].State));
    }

    [Fact]
    public async Task Expanding_a_channel_without_a_tab_adds_the_videos_of_every_tab()
    {
        var results = await AddAsync(new AddItemsRequest(Urls: ["https://fake.test/channel/tabs?count=2"], Paused: true, ExpandPlaylists: PlaylistExpansion.All));

        Assert.Equal(6, results.Count);
        Assert.All(results, r => Assert.Equal(AddItemOutcome.Added, r.Outcome));
        Assert.Equal(
            ["fkv00000002", "fkv00000001", "fks00000002", "fks00000001", "fkl00000002", "fkl00000001"],
            (await ListAsync()).Items.Select(i => i.VideoId));
    }

    [Fact]
    public async Task Expanded_entries_are_checked_for_duplicates()
    {
        var queued = (await AddAsync(new AddItemsRequest(VideoIds: ["fkp00000002"], Paused: true)))[0].ItemId;

        var results = await AddAsync(new AddItemsRequest(
            Urls: ["https://fake.test/playlist?count=2", "https://fake.test/playlist?count=1"],
            VideoIds: ["fkp00000002"],
            Paused: true,
            ExpandPlaylists: PlaylistExpansion.All));

        Assert.Equal(
            [
                (AddItemOutcome.Duplicate, ItemCodes.AlreadyQueued, queued),
                (AddItemOutcome.Added, (string?)null, results[1].ItemId),
                (AddItemOutcome.Duplicate, ItemCodes.DuplicateInRequest, null),
                (AddItemOutcome.Duplicate, ItemCodes.DuplicateInRequest, null),
            ],
            results.Select(r => (r.Outcome, r.Message?.Code, r.ItemId)));
    }

    [Fact]
    public async Task Expansion_keeps_video_URLs_and_resolves_other_single_videos()
    {
        var results = await AddAsync(new AddItemsRequest(
            Urls: ["https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://fake.test/success?id=fkresolve02&title=Tek"],
            Paused: true,
            ExpandPlaylists: PlaylistExpansion.All));

        Assert.All(results, r => Assert.Equal((AddItemOutcome.Added, (string?)null), (r.Outcome, r.Playlist)));
        var items = (await ListAsync()).Items;
        Assert.Equal([("dQw4w9WgXcQ", (string?)null), ("fkresolve02", "Tek")], items.Select(i => (i.VideoId, i.Title)));
    }

    [Fact]
    public async Task Expansion_reports_failed_empty_and_too_large_playlists()
    {
        var results = await AddAsync(new AddItemsRequest(
            Urls: ["https://fake.test/channel/members", "https://fake.test/playlist?count=0", $"https://fake.test/playlist?count={ResolveLimits.MaxEntries + 1}"],
            Paused: true,
            ExpandPlaylists: PlaylistExpansion.All));

        Assert.Equal(
            [
                (AddItemOutcome.Failed, ErrorCodes.PlaylistNotFound),
                (AddItemOutcome.Invalid, ItemCodes.PlaylistEmpty),
                (AddItemOutcome.Invalid, ItemCodes.PlaylistTooLarge),
            ],
            results.Select(r => (r.Outcome, r.Message?.Code)));
        Assert.Equal("https://fake.test/channel/members", results[0].Input);
        Assert.Empty((await ListAsync()).Items);
    }

    [Fact]
    public async Task A_picker_selection_adds_the_chosen_video_ids()
    {
        var resolved = await ResolveAsync("https://fake.test/playlist?count=4");
        var chosen = resolved.Entries.Where((_, i) => i % 2 == 0).Select(e => e.VideoId!).ToList();

        var results = await AddAsync(new AddItemsRequest(VideoIds: chosen, Paused: true));

        Assert.All(results, r => Assert.Equal(AddItemOutcome.Added, r.Outcome));
        Assert.Equal(["fkp00000004", "fkp00000002"], (await ListAsync()).Items.Select(i => i.VideoId));
    }

    private async Task<ResolveResponse> ResolveAsync(string url, int? limit = null)
    {
        using var response = await _client.PostAsJsonAsync(ApiRoutes.Resolve, new ResolveRequest(url, limit), TestJson.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestJson.ReadAsync<ResolveResponse>(response);
    }

    private async Task<EngineMessage> ResolveErrorAsync(ResolveRequest request, HttpStatusCode expected)
    {
        using var response = await _client.PostAsJsonAsync(ApiRoutes.Resolve, request, TestJson.Options, Ct);
        Assert.Equal(expected, response.StatusCode);
        return await TestJson.ReadErrorAsync(response);
    }

    private async Task<IReadOnlyList<AddItemResult>> AddAsync(AddItemsRequest request)
    {
        using var response = await _client.PostAsJsonAsync(ApiRoutes.Items, request, TestJson.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await TestJson.ReadAsync<AddItemsResponse>(response)).Results;
    }

    private async Task<ItemList> ListAsync() =>
        (await _client.GetFromJsonAsync<ItemList>(ApiRoutes.Items, TestJson.Options, Ct))!;

    private async Task WaitForStateAsync(long id, ItemState state)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var item = await _client.GetFromJsonAsync<ItemDetail>(ApiRoutes.ItemPath(ApiRoutes.Item, id), TestJson.Options, timeout.Token);
            if (item!.Item.State == state)
            {
                return;
            }

            await Task.Delay(50, timeout.Token);
        }
    }
}

/// <summary><c>POST /resolve</c> while the tools are missing (the default <see cref="TestEngine"/> has an offline tool feed).</summary>
public sealed class ResolveWithoutToolsTests
{
    [Fact]
    public async Task Resolve_answers_503_and_expansion_fails_while_the_tools_are_missing()
    {
        await using var engine = await TestEngine.StartAsync();
        using var client = engine.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var response = await client.PostAsJsonAsync(ApiRoutes.Resolve, new ResolveRequest("https://fake.test/playlist"), TestJson.Options, ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await TestJson.ReadErrorAsync(response);
        Assert.Equal((ResolveCodes.ToolsMissing, "https://fake.test/playlist"), (error.Code, error.Args["input"]));

        using var add = await client.PostAsJsonAsync(
            ApiRoutes.Items,
            new AddItemsRequest(Urls: ["https://fake.test/playlist"], ExpandPlaylists: PlaylistExpansion.All),
            TestJson.Options,
            ct);
        var result = Assert.Single((await TestJson.ReadAsync<AddItemsResponse>(add)).Results);
        Assert.Equal((AddItemOutcome.Failed, ResolveCodes.ToolsMissing), (result.Outcome, result.Message?.Code));
    }
}
