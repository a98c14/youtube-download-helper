using Microsoft.EntityFrameworkCore;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Core.Tests.Persistence;

public sealed class SettingsStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Defaults_match_the_plan()
    {
        var defaults = EngineSettings.Defaults(@"C:\Users\Iİıi");

        Assert.Equal(@"C:\Users\Iİıi\Downloads\Youtube İndirgeç", defaults.DownloadFolder);
        Assert.Equal("best", defaults.DefaultPresetId);
        Assert.Equal(2, defaults.Concurrency);
        Assert.Equal(47815, defaults.ApiPort);
        Assert.Equal(YtDlpChannel.Stable, defaults.YtDlpChannel);
        Assert.False(defaults.KeepDownloadingAfterExit);
        Assert.Equal("tr-TR", defaults.UiLanguage);
        Assert.True(defaults.CloseToTray);
    }

    [Fact]
    public async Task Empty_table_loads_the_defaults()
    {
        await using var root = await new TestDataRoot().InitializedAsync();

        Assert.Equal(root.Defaults, await root.Settings.LoadAsync(Ct));
    }

    [Fact]
    public async Task Every_setting_round_trips()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        var changed = new EngineSettings
        {
            DownloadFolder = @"D:\İndirilenler\Şarkılar ığüşöç",
            DefaultPresetId = "audio-mp3",
            Concurrency = 3,
            ApiPort = 50000,
            YtDlpChannel = YtDlpChannel.Nightly,
            KeepDownloadingAfterExit = true,
            UiLanguage = "en-US",
            CloseToTray = false,
        };

        await root.Settings.SaveAsync(changed, Ct);

        Assert.Equal(changed, await root.Settings.LoadAsync(Ct));
    }

    [Fact]
    public async Task Only_settings_that_differ_from_the_defaults_are_stored()
    {
        await using var root = await new TestDataRoot().InitializedAsync();

        await root.Settings.SaveAsync(root.Defaults with { YtDlpChannel = YtDlpChannel.Nightly }, Ct);

        await using var db = root.CreateContext();
        var row = Assert.Single(await db.Settings.ToListAsync(Ct));
        Assert.Equal("ytDlpChannel", row.Key);
        Assert.Equal("\"Nightly\"", row.ValueJson);
        Assert.Equal(root.Time.Now, row.UpdatedAt);
    }

    [Fact]
    public async Task A_setting_changed_back_to_its_default_stays_stored()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        await root.Settings.SaveAsync(root.Defaults with { CloseToTray = false }, Ct);

        await root.Settings.SaveAsync(root.Defaults, Ct);

        await using var db = root.CreateContext();
        Assert.Equal("true", (await db.Settings.SingleAsync(Ct)).ValueJson);
        Assert.Equal(root.Defaults, await root.Settings.LoadAsync(Ct));
    }

    [Theory]
    [InlineData("concurrency", "not json")]
    [InlineData("concurrency", "\"two\"")]
    [InlineData("ytDlpChannel", "\"Beta\"")]
    [InlineData("ytDlpChannel", "1")]
    [InlineData("downloadFolder", "null")]
    [InlineData("noSuchSetting", "42")]
    public async Task Unreadable_rows_fall_back_to_the_default_without_affecting_others(string key, string json)
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        await using (var db = root.CreateContext())
        {
            db.Settings.Add(new SettingEntity { Key = key, ValueJson = json });
            db.Settings.Add(new SettingEntity { Key = "apiPort", ValueJson = "50001" });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(root.Defaults with { ApiPort = 50001 }, await root.Settings.LoadAsync(Ct));
    }
}
