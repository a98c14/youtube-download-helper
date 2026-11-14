using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Events;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Engine.Tests.Events;
using YoutubeDownloadHelper.Tests.Shared;

namespace YoutubeDownloadHelper.Engine.Tests.Endpoints;

/// <summary>
/// <c>POST /auth/verify</c>, the browser-refresh lease (<c>POST /auth/refresh/begin</c>, <c>PUT /auth/refresh/complete</c>)
/// and the events of the recovery ladder (PLAN.md §5.7, §6.2, §6.3), against FakeYtDlp: logged in means a non-empty
/// <c>LOGIN_INFO</c> and no <c>*PSIDTS</c> ending in <c>-dead</c>.
/// </summary>
public sealed class AuthRefreshEndpointTests
{
    private const long NextYear = 1_830_000_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Verify_answers_the_verified_status()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = engine.CreateClient();
        Assert.Equal(new AuthVerifyResponse(new AuthInfo(AuthStatus.NotLoggedIn), false), await VerifyAsync(client));

        (await PutCookiesAsync(client, Export("login-value", "psidts-0"))).Dispose();

        Assert.Equal(new AuthVerifyResponse(new AuthInfo(AuthStatus.Valid), false), await VerifyAsync(client));
        Assert.True(engine.App.Services.GetRequiredService<CookieJarStore>().Current!.IsKnownGood);
    }

    [Fact]
    public async Task Dead_cookies_ask_the_connected_client_for_a_refresh_then_expire_with_a_login_needed_notification()
    {
        await using var engine = await TestEngine.StartAsync(
            fakeTools: true,
            configureServices: s => s.AddSingleton(new AuthOptions { RefreshStartTimeout = TimeSpan.FromMilliseconds(200) }));
        await using var events = await SseClient.ConnectAsync(engine);
        using var client = engine.CreateClient();

        (await PutCookiesAsync(client, Export("login-value", "psidts-0-dead"))).Dispose();

        await events.WaitForAsync(EventTypes.AuthRefreshRequested);
        var expired = await events.WaitForAsync(
            m => m.Type == EventTypes.AuthChanged && TestJson.Read<AuthChangedData>(m.Data).Auth.Status == AuthStatus.Expired,
            "auth.changed to expired");
        var notify = await events.WaitForAsync(EventTypes.Notify);
        Assert.True(notify.Id > expired.Id);
        Assert.Equal(NotifyCodes.AuthLoginNeeded, TestJson.Read<NotifyData>(notify.Data).Message.Code);
        Assert.Equal(AuthStatus.Expired, (await client.GetFromJsonAsync<AuthInfo>(ApiRoutes.Auth, TestJson.Options, Ct))!.Status);
    }

    [Fact]
    public async Task A_refresh_round_trip_keeps_a_valid_jar_and_rejects_an_invalid_one()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = engine.CreateClient();
        var store = engine.App.Services.GetRequiredService<CookieJarStore>();

        using (var none = await client.PostAsync(ApiRoutes.AuthRefreshBegin, null, Ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
            Assert.Equal(AuthCodes.NotLoggedIn, (await TestJson.ReadErrorAsync(none)).Code);
        }

        (await PutCookiesAsync(client, Export("login-value", "psidts-0"))).Dispose();
        var login = store.Current!;

        var begun = await BeginAsync(client);
        Assert.Contains(new CookieInfo(".youtube.com", "/", "SID", "sid-value", true, true, DateTimeOffset.FromUnixTimeSeconds(NextYear)), begun.Cookies);
        Assert.Contains(begun.Cookies, c => c is { Domain: ".youtube.com", Name: "__Secure-3PSIDTS", Value: "psidts-0" });
        using (var busy = await client.PostAsync(ApiRoutes.AuthRefreshBegin, null, Ct))
        {
            Assert.Equal(AuthCodes.RefreshInProgress, (await TestJson.ReadErrorAsync(busy)).Code);
        }

        var refreshed = Rotated(begun.Cookies, "psidts-1");
        using (var wrongLease = await CompleteAsync(client, new AuthRefreshCompleteRequest("wrong", refreshed)))
        {
            Assert.Equal(HttpStatusCode.Conflict, wrongLease.StatusCode);
            Assert.Equal(AuthCodes.RefreshNoLease, (await TestJson.ReadErrorAsync(wrongLease)).Code);
        }

        using (var accepted = await CompleteAsync(client, new AuthRefreshCompleteRequest(begun.LeaseId, refreshed)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var body = await TestJson.ReadAsync<AuthRefreshCompleteResponse>(accepted);
            Assert.Equal((AuthStatus.Valid, begun.Cookies.Count), (body.Auth.Status, body.Stored));
        }

        Assert.Equal((CookieSource.BrowserRefresh, (long?)login.Number), (store.Current!.Source, store.Current.BasedOn));
        using (var again = await CompleteAsync(client, new AuthRefreshCompleteRequest(begun.LeaseId, refreshed)))
        {
            Assert.Equal(AuthCodes.RefreshNoLease, (await TestJson.ReadErrorAsync(again)).Code);
        }

        var second = await BeginAsync(client);
        var loggedOut = second.Cookies.Select(c => c.Name == "LOGIN_INFO" ? c with { Value = "" } : c).ToList();
        using (var rejected = await CompleteAsync(client, new AuthRefreshCompleteRequest(second.LeaseId, loggedOut)))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            Assert.Equal(AuthCodes.RefreshRejected, (await TestJson.ReadErrorAsync(rejected)).Code);
        }

        Assert.Equal("psidts-1", store.Current!.Jar.Find(".youtube.com", "/", "__Secure-3PSIDTS")!.Value);

        // The lease ended with the rejection: a new refresh can begin.
        Assert.False(string.IsNullOrEmpty((await BeginAsync(client)).LeaseId));
    }

    [Fact]
    public async Task Complete_validates_its_body()
    {
        await using var engine = await TestEngine.StartAsync(fakeTools: true);
        using var client = engine.CreateClient();
        (await PutCookiesAsync(client, Export("login-value", "psidts-0"))).Dispose();
        var begun = await BeginAsync(client);

        using var noLease = await CompleteAsync(client, new AuthRefreshCompleteRequest(" ", begun.Cookies));
        using var badCookie = await CompleteAsync(client, new AuthRefreshCompleteRequest(begun.LeaseId, [new CookieInfo(".youtube.com", "/", "SID", "a\tb")]));
        using var google = await CompleteAsync(client, new AuthRefreshCompleteRequest(begun.LeaseId, [new CookieInfo(".google.com", "/", "SID", "x")]));

        Assert.Equal(("leaseId", "cookies"), ((await TestJson.ReadErrorAsync(noLease)).Args["parameter"], (await TestJson.ReadErrorAsync(badCookie)).Args["parameter"]));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, AuthCodes.NoYouTubeCookies), (google.StatusCode, (await TestJson.ReadErrorAsync(google)).Code));

        // None of these touched the lease.
        using var completed = await CompleteAsync(client, new AuthRefreshCompleteRequest(begun.LeaseId, Rotated(begun.Cookies, "psidts-1")));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    private static string Export(string loginInfo, string psidts) =>
        $"#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t{NextYear}\tSID\tsid-value\n"
        + $".youtube.com\tTRUE\t/\tTRUE\t{NextYear}\tLOGIN_INFO\t{loginInfo}\n"
        + $"#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t{NextYear}\t__Secure-3PSIDTS\t{psidts}\n"
        + $".youtube.com\tTRUE\t/\tTRUE\t{NextYear}\tSIDCC\tsidcc-0\n";

    private static List<CookieInfo> Rotated(IEnumerable<CookieInfo> cookies, string psidts) =>
        [.. cookies.Select(c => c.Name == "__Secure-3PSIDTS" ? c with { Value = psidts } : c)];

    private static async Task<HttpResponseMessage> PutCookiesAsync(HttpClient client, string netscape)
    {
        var response = await client.PutAsJsonAsync(ApiRoutes.AuthCookies, new SetCookiesRequest(Netscape: netscape), TestJson.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response;
    }

    private static async Task<AuthVerifyResponse> VerifyAsync(HttpClient client)
    {
        using var response = await client.PostAsync(ApiRoutes.AuthVerify, null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestJson.ReadAsync<AuthVerifyResponse>(response);
    }

    private static async Task<AuthRefreshBeginResponse> BeginAsync(HttpClient client)
    {
        using var response = await client.PostAsync(ApiRoutes.AuthRefreshBegin, null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestJson.ReadAsync<AuthRefreshBeginResponse>(response);
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, AuthRefreshCompleteRequest request) =>
        client.PutAsJsonAsync(ApiRoutes.AuthRefreshComplete, request, TestJson.Options, Ct);
}
