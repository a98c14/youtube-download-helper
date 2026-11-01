using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using YoutubeDownloadHelper.Contracts.Api;

namespace YoutubeDownloadHelper.Engine.Tests.Hosting;

public sealed class ErrorResponseTests
{
    private const string Secret = "internal detail that must not leak";

    [Fact]
    public async Task Unhandled_exception_is_500_with_a_code_and_no_details()
    {
        await using var engine = await TestEngine.StartAsync(mapExtra: app =>
        {
            app.MapGet("/api/v1/test/throw", string () => throw new InvalidOperationException(Secret));
        });
        using var client = engine.CreateClient();

        using var response = await client.GetAsync("/api/v1/test/throw", TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        await RequestGuardTests.AssertErrorAsync(response, HttpStatusCode.InternalServerError, ApiErrorCodes.Internal);
    }

    [Fact]
    public async Task Other_status_without_a_body_gets_a_generic_code_with_the_status()
    {
        await using var engine = await TestEngine.StartAsync(mapExtra: app =>
            app.MapGet("/api/v1/test/teapot", () => Results.StatusCode(StatusCodes.Status418ImATeapot)));
        using var client = engine.CreateClient();

        using var response = await client.GetAsync("/api/v1/test/teapot", TestContext.Current.CancellationToken);

        await RequestGuardTests.AssertErrorAsync(response, (HttpStatusCode)418, ApiErrorCodes.HttpError);
    }
}
