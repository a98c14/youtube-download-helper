using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Api;
using YoutubeDownloadHelper.Contracts.Messages;

namespace YoutubeDownloadHelper.Engine.Tests.Hosting;

public sealed class RequestGuardTests : IAsyncLifetime
{
    private TestEngine _engine = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _engine = await TestEngine.StartAsync();

    public ValueTask DisposeAsync() => _engine.DisposeAsync();

    [Theory]
    [InlineData(ApiRoutes.Info)]
    [InlineData(ApiRoutes.OpenApiDocument)]
    [InlineData("/no/such/route")]
    public async Task Request_without_token_is_401(string path)
    {
        using var client = _engine.CreateAnonymousClient();
        using var response = await client.GetAsync(path, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.Unauthorized);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task Shutdown_without_token_is_401_and_does_not_stop()
    {
        using var client = _engine.CreateAnonymousClient();
        using var response = await client.PostAsync(ApiRoutes.EngineShutdown, null, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.Unauthorized);
        Assert.False(_engine.App.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Theory]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("token")]
    public async Task Request_with_a_bad_authorization_header_is_401(string authorization)
    {
        using var client = _engine.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Info);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Token_of_another_engine_is_401()
    {
        await using var other = await TestEngine.StartAsync();
        using var client = _engine.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.Token.Value);

        using var response = await client.GetAsync(ApiRoutes.Info, Ct);

        Assert.NotEqual(_engine.Token.Value, other.Token.Value);
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.Unauthorized);
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    public async Task Scheme_is_case_insensitive(string scheme)
    {
        using var client = _engine.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, _engine.Token.Value);

        using var response = await client.GetAsync(ApiRoutes.Info, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1:47815")]
    [InlineData("localhost:47815")]
    [InlineData("LOCALHOST:47815")]
    public async Task Allowed_hosts_pass(string host)
    {
        using var client = _engine.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Info);
        request.Headers.Host = host;

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("evil.example:47815")]
    [InlineData("evil.example")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:80")]
    [InlineData("localhost:47816")]
    [InlineData("127.0.0.2:47815")]
    [InlineData("[::1]:47815")]
    [InlineData("0.0.0.0:47815")]
    [InlineData("localhost.evil.example:47815")]
    public async Task Other_hosts_are_403_even_with_the_token(string host)
    {
        using var client = _engine.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Info);
        request.Headers.Host = host;

        using var response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, ApiErrorCodes.HostNotAllowed);
    }

    [Theory]
    [InlineData("http://127.0.0.1:47815")]
    [InlineData("http://localhost:47815")]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    public async Task Any_origin_is_403_even_with_the_token(string origin)
    {
        using var client = _engine.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Info);
        request.Headers.TryAddWithoutValidation("Origin", origin);

        using var response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, ApiErrorCodes.OriginNotAllowed);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cors_preflight_is_403_without_cors_headers()
    {
        using var client = _engine.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, ApiRoutes.EngineShutdown);
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");

        using var response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, ApiErrorCodes.OriginNotAllowed);
        Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unknown_route_with_token_is_404_with_a_code()
    {
        using var client = _engine.CreateClient();
        using var response = await client.GetAsync(ApiRoutes.Base + "/no-such-route", Ct);

        await AssertErrorAsync(response, HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
    }

    [Fact]
    public async Task Wrong_method_is_405_with_a_code()
    {
        using var client = _engine.CreateClient();
        using var response = await client.GetAsync(ApiRoutes.EngineShutdown, Ct);

        await AssertErrorAsync(response, HttpStatusCode.MethodNotAllowed, ApiErrorCodes.MethodNotAllowed);
        Assert.False(_engine.App.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    internal static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<EngineMessage>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(code, body.Code);
        Assert.NotNull(body.Args);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
    }
}
