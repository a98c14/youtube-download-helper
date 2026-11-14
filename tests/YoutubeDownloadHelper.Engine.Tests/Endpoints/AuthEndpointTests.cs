using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Api;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Events;
using YoutubeDownloadHelper.Contracts.Info;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Engine.Tests.Events;

namespace YoutubeDownloadHelper.Engine.Tests.Endpoints;

/// <summary><c>GET /auth</c>, <c>PUT</c> and <c>DELETE /auth/cookies</c>, and the per-job cookies file seen from the API (PLAN.md §5.7).</summary>
public sealed class AuthEndpointTests
{
    private const long NextYear = 1_830_000_000;

    // A cookies.txt export: YouTube cookies (one HttpOnly, one session cookie), a Google cookie and a broken line.
    private static readonly string Export =
        "# Netscape HTTP Cookie File\n"
        + "# https://curl.se/docs/http-cookies.html\n"
        + $"#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t{NextYear}\tSID\tsid-value\n"
        + $".youtube.com\tTRUE\t/\tTRUE\t{NextYear}\tLOGIN_INFO\tlogin-value\n"
        + $".youtube.com\tTRUE\t/\tTRUE\t{NextYear}\tSIDCC\tsidcc-0\n"
        + $".google.com\tTRUE\t/\tTRUE\t{NextYear}\tSID\tgoogle-sid\n"
        + "not a cookie line\n"
        + "www.youtube.com\tFALSE\t/\tFALSE\t0\tYSC\tysc-value\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_cookies_txt_import_is_stored_reported_and_round_trips_into_a_job_file()
    {
        await using var engine = await TestEngine.StartAsync();
        await using var events = await SseClient.ConnectAsync(engine);
        using var client = engine.CreateClient();
        Assert.Equal(new AuthInfo(AuthStatus.NotLoggedIn), await GetAuthAsync(client));

        var response = await PutAsync(client, new SetCookiesRequest(Netscape: Export), HttpStatusCode.OK);
        var result = await TestJson.ReadAsync<SetCookiesResponse>(response);

        Assert.Equal((AuthStatus.Unknown, 4), (result.Auth.Status, result.Stored));
        Assert.Equal([7], result.SkippedLines);
        Assert.Equal(AuthStatus.Unknown, (await GetAuthAsync(client)).Status);
        Assert.Equal(AuthStatus.Unknown, (await client.GetFromJsonAsync<EngineInfo>(ApiRoutes.Info, TestJson.Options, Ct))!.Auth.Status);
        var changed = await events.WaitForAsync(EventTypes.AuthChanged);
        Assert.Equal(AuthStatus.Unknown, TestJson.Read<AuthChangedData>(changed.Data).Auth.Status);

        var store = engine.App.Services.GetRequiredService<CookieJarStore>();
        Assert.Equal(CookieSource.Import, store.Current!.Source);

        // The file a run gets holds exactly the imported YouTube cookies, flags and expiry included.
        var cookies = engine.App.Services.GetRequiredService<CookieFileProvider>();
        var path = (await cookies.BeforeResolveAsync(Ct))!;
        var written = NetscapeCookieFile.Parse(await File.ReadAllTextAsync(path, Ct)).Jar;
        Assert.True(NetscapeCookieFile.Parse(Export).Jar.YouTubeOnly().SequenceEqual(written));
        Assert.True(written.Find(".youtube.com", "/", "SID")!.HttpOnly);
        Assert.Null(written.Find("www.youtube.com", "/", "YSC")!.Expires);
        await cookies.AfterResolveAsync(path, failedWithAuth: false, Ct);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task The_login_windows_cookies_are_stored_as_a_login_and_log_out_deletes_them()
    {
        await using var engine = await TestEngine.StartAsync();
        await using var events = await SseClient.ConnectAsync(engine);
        using var client = engine.CreateClient();
        var expires = DateTimeOffset.FromUnixTimeSeconds(NextYear);

        var response = await PutAsync(
            client,
            new SetCookiesRequest(
            [
                new CookieInfo(".youtube.com", "/", "SID", "sid-value", Secure: true, HttpOnly: true, Expires: expires),
                new CookieInfo("www.youtube.com", "/", "YSC", "ysc-value", Secure: true),
                new CookieInfo(".google.com.tr", "/", "NID", "nid-value", Expires: expires),
            ]),
            HttpStatusCode.OK);

        Assert.Equal(2, (await TestJson.ReadAsync<SetCookiesResponse>(response)).Stored);
        var store = engine.App.Services.GetRequiredService<CookieJarStore>();
        var jar = store.Current!.Jar;
        Assert.Equal(CookieSource.Login, store.Current.Source);
        Assert.Equal(
            new JarCookie(".youtube.com", "/", "SID", "sid-value") { IncludeSubdomains = true, Secure = true, HttpOnly = true, Expires = expires },
            jar.Find(".youtube.com", "/", "SID"));
        Assert.Equal(
            new JarCookie("www.youtube.com", "/", "YSC", "ysc-value") { Secure = true },
            jar.Find("www.youtube.com", "/", "YSC"));

        using var logout = await client.DeleteAsync(ApiRoutes.AuthCookies, Ct);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(new AuthInfo(AuthStatus.NotLoggedIn), await GetAuthAsync(client));
        Assert.Null(store.Current);
        Assert.False(File.Exists(engine.Paths.CookiesBin));
        await events.WaitForAsync(EventTypes.AuthChanged);
        var loggedOut = await events.WaitForAsync(EventTypes.AuthChanged);
        Assert.Equal(AuthStatus.NotLoggedIn, TestJson.Read<AuthChangedData>(loggedOut.Data).Auth.Status);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"cookies":[],"netscape":""}""")]
    [InlineData("""{"cookies":[{"domain":".youtube.com","path":"/","name":"SID","value":"a\tb"}]}""")]
    [InlineData("""{"cookies":[{"domain":" ","path":"/","name":"SID","value":"x"}]}""")]
    [InlineData("""{"cookies":[null]}""")]
    public async Task Put_rejects_a_body_without_exactly_one_valid_source(string body)
    {
        await using var engine = await TestEngine.StartAsync();
        using var client = engine.CreateClient();

        using var response = await client.PutAsync(ApiRoutes.AuthCookies, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await TestJson.ReadErrorAsync(response);
        Assert.Equal((ApiErrorCodes.InvalidParameter, "cookies"), (error.Code, error.Args["parameter"]));
        Assert.Null(engine.App.Services.GetRequiredService<CookieJarStore>().Current);
    }

    [Fact]
    public async Task Put_without_youtube_cookies_is_422_and_keeps_the_stored_ones()
    {
        await using var engine = await TestEngine.StartAsync();
        using var client = engine.CreateClient();
        (await PutAsync(client, new SetCookiesRequest(Netscape: Export), HttpStatusCode.OK)).Dispose();

        using var response = await PutAsync(
            client, new SetCookiesRequest(Netscape: $".google.com\tTRUE\t/\tTRUE\t{NextYear}\tSID\tgoogle-sid\n"), HttpStatusCode.UnprocessableEntity);

        Assert.Equal(AuthCodes.NoYouTubeCookies, (await TestJson.ReadErrorAsync(response)).Code);
        Assert.Equal(CookieSource.Import, engine.App.Services.GetRequiredService<CookieJarStore>().Current!.Source);
    }

    [Fact]
    public async Task A_job_merges_its_rotated_cookies_and_a_login_releases_items_waiting_on_auth()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        await using var events = await SseClient.ConnectAsync(engine);
        using var client = engine.CreateClient();

        // Without cookies, members-only content waits for auth.
        var waiting = await AddAsync(client, "https://fake.test/members-only?id=authwait001");
        await WaitForStateAsync(events, waiting, ItemState.WaitingAuth);

        (await PutAsync(client, new SetCookiesRequest(Netscape: Export), HttpStatusCode.OK)).Dispose();
        await WaitForStateAsync(events, waiting, ItemState.Completed);

        var rotating = await AddAsync(client, "https://fake.test/cookie-writeback?id=authrot0001&rotate=SIDCC");
        await WaitForStateAsync(events, rotating, ItemState.Completed);

        var current = engine.App.Services.GetRequiredService<CookieJarStore>().Current!;
        Assert.Equal((CookieSource.Writeback, "sidcc-0-rotated"), (current.Source, current.Jar.Find(".youtube.com", "/", "SIDCC")!.Value));
        Assert.True(current.Jar.Find(".youtube.com", "/", "SID")!.HttpOnly, "The stored attributes stay.");
        Assert.Equal(0, engine.App.Services.GetRequiredService<CookieFileProvider>().Outstanding);
        Assert.False(Directory.Exists(engine.Paths.WorkDirFor(rotating)));
    }

    private static async Task<AuthInfo> GetAuthAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<AuthInfo>(ApiRoutes.Auth, TestJson.Options, Ct))!;

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, SetCookiesRequest request, HttpStatusCode expected)
    {
        var response = await client.PutAsJsonAsync(ApiRoutes.AuthCookies, request, TestJson.Options, Ct);
        Assert.Equal(expected, response.StatusCode);
        return response;
    }

    private static async Task<long> AddAsync(HttpClient client, string url)
    {
        using var response = await client.PostAsJsonAsync(ApiRoutes.Items, new AddItemsRequest(Urls: [url]), TestJson.Options, Ct);
        var result = Assert.Single((await TestJson.ReadAsync<AddItemsResponse>(response)).Results);
        Assert.Equal(AddItemOutcome.Added, result.Outcome);
        return result.ItemId!.Value;
    }

    private static Task<SseMessage> WaitForStateAsync(SseClient events, long itemId, ItemState state) =>
        events.WaitForAsync(
            m => m.Type == EventTypes.ItemUpdated && TestJson.Read<ItemUpdatedData>(m.Data).Item is { } item && item.Id == itemId && item.State == state,
            $"item {itemId} to be {state}");
}
