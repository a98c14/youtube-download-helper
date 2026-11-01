using System.Net;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Tests.Shared;

namespace YoutubeDownloadHelper.Engine.Tests.Endpoints;

/// <summary>
/// The OpenAPI document is the contract for non-.NET clients (PLAN.md §11): any change to it shows up here and has to
/// be approved by updating <c>Endpoints/Snapshots/openapi-v1.approved.json</c>.
/// </summary>
public sealed class OpenApiTests
{
    [Fact]
    public async Task OpenApi_document_matches_the_snapshot()
    {
        await using var engine = await TestEngine.StartAsync();
        using var client = engine.CreateClient();

        using var response = await client.GetAsync(ApiRoutes.OpenApiDocument, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Approval.Verify(json, Path.Combine("Endpoints", "Snapshots"), "openapi-v1", "json");
    }
}
