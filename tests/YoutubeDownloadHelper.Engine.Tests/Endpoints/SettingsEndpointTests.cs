using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Api;
using YoutubeDownloadHelper.Contracts.Presets;
using YoutubeDownloadHelper.Contracts.Settings;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Engine.Tests.Endpoints;

public sealed class SettingsEndpointTests : IAsyncLifetime
{
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
    public async Task Get_returns_the_defaults()
    {
        var defaults = _engine.App.Services.GetRequiredService<SettingsStore>().Defaults;

        var settings = await _client.GetFromJsonAsync<SettingsInfo>(ApiRoutes.Settings, TestJson.Options, Ct);

        Assert.Equal(
            new SettingsInfo(defaults.DownloadFolder, PresetIds.Best, 2, EngineSettings.DefaultApiPort, YtDlpChannel.Stable, false, UiLanguages.Turkish, true),
            settings);
    }

    [Fact]
    public async Task Patch_changes_only_the_given_settings_and_stores_them()
    {
        var folder = Path.Combine(_engine.Paths.Root, "İndirilenler");

        using var response = await PatchAsync(
            $$"""{"downloadFolder":{{System.Text.Json.JsonSerializer.Serialize(folder)}},"keepDownloadingAfterExit":true,"ytDlpChannel":"nightly","uiLanguage":"en-US"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await TestJson.ReadAsync<SettingsInfo>(response);
        var stored = await _engine.App.Services.GetRequiredService<SettingsStore>().LoadAsync(Ct);
        Assert.Equal((folder, true, YtDlpChannel.Nightly, UiLanguages.English, PresetIds.Best), (returned.DownloadFolder, returned.KeepDownloadingAfterExit, returned.YtDlpChannel, returned.UiLanguage, returned.DefaultPresetId));
        Assert.Equal((folder, true, YtDlpChannel.Nightly, UiLanguages.English), (stored.DownloadFolder, stored.KeepDownloadingAfterExit, stored.YtDlpChannel, stored.UiLanguage));
    }

    [Theory]
    [InlineData("""{"defaultPresetId":"8k","uiLanguage":"en-US"}""", "defaultPresetId")]
    [InlineData("""{"downloadFolder":"relative\\folder"}""", "downloadFolder")]
    [InlineData("""{"apiPort":70000}""", "apiPort")]
    [InlineData("""{"uiLanguage":"de-DE"}""", "uiLanguage")]
    [InlineData("""{"ytDlpChannel":7}""", "ytDlpChannel")]
    public async Task Patch_rejects_an_invalid_value_and_changes_nothing(string body, string parameter)
    {
        using var response = await PatchAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await TestJson.ReadErrorAsync(response);
        Assert.Equal((ApiErrorCodes.InvalidParameter, parameter), (error.Code, error.Args["parameter"]));
        var stored = await _engine.App.Services.GetRequiredService<SettingsStore>().LoadAsync(Ct);
        Assert.Equal(UiLanguages.Turkish, stored.UiLanguage);
    }

    private Task<HttpResponseMessage> PatchAsync(string json) =>
        _client.PatchAsync(ApiRoutes.Settings, new StringContent(json, Encoding.UTF8, "application/json"), Ct);
}
