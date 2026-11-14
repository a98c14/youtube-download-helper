using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Settings;
using YoutubeDownloadHelper.Contracts.Tools;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Tests.Tools;

/// <summary>Update checks, yt-dlp channels, rollback and garbage collection (PLAN.md §5.6), against a fake feed.</summary>
public sealed class ToolUpdateTests
{
    private static readonly JobFailure ToolBroken =
        new(ItemTrigger.WaitForToolFix, ErrorCategory.ToolBroken, new EngineMessage("error.toolBroken.extractor", "x"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_update_check_installs_newer_releases_and_requeues_the_items_waiting_for_a_tool_fix()
    {
        await using var h = await InstalledAsync("1.0");
        var item = await WaitingForToolFixAsync(h);
        h.Feed.Publish(ManagedTool.YtDlp, "1.1");
        h.Queue.DrainEvents();

        Assert.True(await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct));

        Assert.Equal(YtDlpPath(h, "1.1"), h.Manager.Current.YtDlp);
        Assert.Equal(Path.Combine(h.VersionDir(ManagedTool.Deno, "1.0"), "deno.exe"), h.Manager.Current.Deno);
        Assert.Equal(4, h.Feed.Downloads);
        Assert.Equal(ItemState.Queued, (await h.Queue.ItemAsync(item)).State);
        var events = h.Queue.DrainEvents();
        Assert.Contains(events, e => e is ToolInstalledEvent { Tool: "yt-dlp", Version: "1.1", PreviousVersion: "1.0" });
        Assert.IsType<ToolsCheckedEvent>(events[^1]);
        foreach (var tool in ManagedTool.All)
        {
            Assert.Equal(h.Queue.Root.Time.Now, (await h.RowAsync(tool))!.LastCheck);
        }
    }

    [Fact]
    public async Task Nothing_newer_installs_nothing_and_keeps_the_items_waiting()
    {
        await using var h = await InstalledAsync("2026.10.26");
        var item = await WaitingForToolFixAsync(h);

        // An older build at the top of the feed (a pulled release) is no update either.
        h.Feed.Publish(ManagedTool.YtDlp, "2026.10.9");

        Assert.True(await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct));

        Assert.Equal(3, h.Feed.Downloads);
        Assert.Equal(YtDlpPath(h, "2026.10.26"), h.Manager.Current.YtDlp);
        Assert.Equal(ItemState.WaitingToolFix, (await h.Queue.ItemAsync(item)).State);
    }

    [Fact]
    public async Task An_offline_update_check_reports_it_and_changes_nothing()
    {
        await using var h = await InstalledAsync("1.0");
        h.Feed.Offline = true;
        h.Queue.DrainEvents();

        Assert.False(await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct));

        Assert.True(h.Manager.ToolsReady);
        Assert.Equal(
            [ToolCodes.DownloadFailed, ToolCodes.DownloadFailed, ToolCodes.DownloadFailed],
            h.Queue.DrainEvents().OfType<ToolInstallFailedEvent>().Select(e => e.Reason.Code));
    }

    [Fact]
    public async Task An_update_while_a_job_runs_leaves_its_binary_and_garbage_collects_it_once_the_job_ends()
    {
        await using var h = await InstalledAsync("1.0");
        var job = h.Manager.Current;
        var lease = h.Manager.Use(job);

        h.Feed.Publish(ManagedTool.YtDlp, "2.0");
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        h.Feed.Publish(ManagedTool.YtDlp, "3.0");
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);

        // New runs get 3.0; the running job's 1.0 is neither current nor previous, but in use.
        Assert.Equal(YtDlpPath(h, "3.0"), h.Manager.Current.YtDlp);
        Assert.True(File.Exists(job.YtDlp));
        Assert.True(Directory.Exists(h.VersionDir(ManagedTool.YtDlp, "2.0")));
        var row = await h.RowAsync(ManagedTool.YtDlp);
        Assert.Equal(("3.0", "2.0"), (row!.Current, row.Previous));

        lease.Dispose();
        lease.Dispose();
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);

        Assert.False(Directory.Exists(h.VersionDir(ManagedTool.YtDlp, "1.0")), "The unused old version was kept.");
        Assert.True(Directory.Exists(h.VersionDir(ManagedTool.YtDlp, "2.0")), "The previous version was deleted.");
        Assert.True(File.Exists(h.Manager.Current.YtDlp));
        Assert.True(File.Exists(job.Deno), "The current Deno was deleted.");
    }

    [Fact]
    public async Task Garbage_collection_leaves_other_folders_alone()
    {
        await using var h = await InstalledAsync("1.0");
        var stray = Directory.CreateDirectory(Path.Combine(h.Paths.ToolsDir, "yt-dlp", "0.9")).FullName;
        var notAVersion = Directory.CreateDirectory(Path.Combine(h.Paths.ToolsDir, "yt-dlp", ".keep")).FullName;

        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);

        Assert.False(Directory.Exists(stray));
        Assert.True(Directory.Exists(notAVersion));
        Assert.True(Directory.Exists(h.VersionDir(ManagedTool.YtDlp, "1.0")));
    }

    [Fact]
    public async Task Switching_to_nightly_and_back_installs_each_channels_latest()
    {
        await using var h = await InstalledAsync("2026.10.26");
        h.Feed.Publish(ManagedTool.YtDlp, "2026.11.01.233311", channel: ToolChannels.Nightly);

        // On stable, a newer nightly is not an update.
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        Assert.Equal(YtDlpPath(h, "2026.10.26"), h.Manager.Current.YtDlp);

        await SetChannelAsync(h, YtDlpChannel.Nightly);
        await h.Manager.CheckForUpdatesAsync([ToolNames.YtDlp], Ct);

        Assert.Equal(YtDlpPath(h, "2026.11.01.233311"), h.Manager.Current.YtDlp);
        var row = await h.RowAsync(ManagedTool.YtDlp);
        Assert.Equal(("2026.11.01.233311", "nightly", "2026.10.26", "stable"), (row!.Current, row.Channel, row.Previous, row.PreviousChannel));
        var details = await h.Manager.GetDetailsAsync(Ct);
        Assert.Equal((YtDlpChannel.Nightly, YtDlpChannel.Nightly), (details.YtDlpChannel, details.Tools[0].Channel));

        // Back to stable is a downgrade by number, and still installs.
        await SetChannelAsync(h, YtDlpChannel.Stable);
        await h.Manager.CheckForUpdatesAsync([ToolNames.YtDlp], Ct);

        Assert.Equal(YtDlpPath(h, "2026.10.26"), h.Manager.Current.YtDlp);
        row = await h.RowAsync(ManagedTool.YtDlp);
        Assert.Equal(("2026.10.26", "stable", "2026.11.01.233311", "nightly"), (row!.Current, row.Channel, row.Previous, row.PreviousChannel));

        // And on stable again, nothing more happens.
        var downloads = h.Feed.Downloads;
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        Assert.Equal(downloads, h.Feed.Downloads);
    }

    [Fact]
    public async Task On_nightly_only_a_newer_nightly_is_an_update()
    {
        await using var h = await InstalledAsync("2026.10.26");
        await SetChannelAsync(h, YtDlpChannel.Nightly);
        h.Feed.Publish(ManagedTool.YtDlp, "2026.11.01.233311", channel: ToolChannels.Nightly);
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        h.Feed.Publish(ManagedTool.YtDlp, "2026.12.01", channel: ToolChannels.Stable);

        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        Assert.Equal(YtDlpPath(h, "2026.11.01.233311"), h.Manager.Current.YtDlp);

        h.Feed.Publish(ManagedTool.YtDlp, "2026.11.02.010203", channel: ToolChannels.Nightly);
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        Assert.Equal(YtDlpPath(h, "2026.11.02.010203"), h.Manager.Current.YtDlp);
    }

    [Fact]
    public async Task A_first_install_comes_from_the_configured_channel()
    {
        await using var h = await ToolHarness.StartAsync(f =>
        {
            f.PublishAll("1.0");
            f.Publish(ManagedTool.YtDlp, "1.0.123", channel: ToolChannels.Nightly);
        });
        await SetChannelAsync(h, YtDlpChannel.Nightly);

        Assert.True(await h.Manager.CheckAsync(smoke: true, Ct));

        Assert.Equal(YtDlpPath(h, "1.0.123"), h.Manager.Current.YtDlp);
        Assert.Equal("nightly", (await h.RowAsync(ManagedTool.YtDlp))!.Channel);
    }

    [Fact]
    public async Task A_current_version_that_fails_its_smoke_test_is_rolled_back_and_skipped()
    {
        await using var h = await InstalledAsync("1.0");
        var item = await WaitingForToolFixAsync(h);
        h.Feed.Publish(ManagedTool.YtDlp, "2.0");
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        await h.Queue.Actor.StartResolvingAsync(item, Ct);
        await h.Queue.Actor.FailAsync(item, ToolBroken, Ct);
        await File.WriteAllTextAsync(h.Manager.Current.YtDlp, "broken", Ct);
        h.Queue.DrainEvents();

        Assert.True(await h.Manager.CheckAsync(smoke: true, Ct));

        Assert.Equal(YtDlpPath(h, "1.0"), h.Manager.Current.YtDlp);
        var row = await h.RowAsync(ManagedTool.YtDlp);
        Assert.Equal(("1.0", "2.0", "2.0"), (row!.Current, row.Previous, row.SkippedVersion));
        Assert.Contains(h.Queue.DrainEvents(), e => e is ToolRolledBackEvent { Tool: "yt-dlp", Version: "1.0", FromVersion: "2.0", Automatic: true });
        Assert.Equal(ItemState.Queued, (await h.Queue.ItemAsync(item)).State);
        Assert.Equal(4, h.Feed.Downloads);

        // The version it left is not installed again; a newer one is.
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        Assert.Equal(YtDlpPath(h, "1.0"), h.Manager.Current.YtDlp);
        h.Feed.Publish(ManagedTool.YtDlp, "3.0");
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        Assert.Equal(YtDlpPath(h, "3.0"), h.Manager.Current.YtDlp);
        row = await h.RowAsync(ManagedTool.YtDlp);
        Assert.Equal(("3.0", "1.0"), (row!.Current, row.Previous));
    }

    [Fact]
    public async Task A_yt_dlp_that_cannot_simulate_a_download_is_rolled_back()
    {
        await using var h = await InstalledAsync("1.0");
        h.Feed.Publish(ManagedTool.YtDlp, "2.0");
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        await File.WriteAllTextAsync(h.Manager.Current.YtDlp, "ok but nosim", Ct);

        Assert.True(await h.Manager.CheckAsync(smoke: true, Ct));

        Assert.Equal(YtDlpPath(h, "1.0"), h.Manager.Current.YtDlp);
        var simulation = h.Smoke.LastSimulation!;
        Assert.Equal(ToolManager.SimulateVideoUrl, simulation[^1]);
        Assert.Contains("deno:" + h.Manager.Current.Deno, simulation);
        Assert.Contains(h.Manager.Current.Ffmpeg, simulation);
    }

    [Fact]
    public async Task A_failed_simulation_while_offline_proves_nothing()
    {
        await using var h = await InstalledAsync("1.0");
        h.Feed.Publish(ManagedTool.YtDlp, "2.0");
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        await File.WriteAllTextAsync(h.Manager.Current.YtDlp, "ok but nosim", Ct);
        h.Network.Online = false;

        Assert.True(await h.Manager.CheckAsync(smoke: true, Ct));

        Assert.Equal(YtDlpPath(h, "2.0"), h.Manager.Current.YtDlp);
    }

    [Fact]
    public async Task A_failed_simulation_without_a_working_previous_version_keeps_the_current_one_ready()
    {
        await using var h = await InstalledAsync("1.0");
        await File.WriteAllTextAsync(h.Manager.Current.YtDlp, "ok but nosim", Ct);
        var downloads = h.Feed.Downloads;

        Assert.True(await h.Manager.CheckAsync(smoke: true, Ct));

        Assert.True(h.Manager.ToolsReady);
        Assert.Equal(YtDlpPath(h, "1.0"), h.Manager.Current.YtDlp);
        Assert.Equal(downloads, h.Feed.Downloads);
    }

    [Fact]
    public async Task A_new_release_that_cannot_simulate_a_download_is_rejected()
    {
        await using var h = await InstalledAsync("1.0");
        h.Feed.Publish(ManagedTool.YtDlp, "2.0", content: "ok nosim");
        h.Queue.DrainEvents();

        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);

        Assert.Equal(YtDlpPath(h, "1.0"), h.Manager.Current.YtDlp);
        Assert.False(Directory.Exists(h.VersionDir(ManagedTool.YtDlp, "2.0")));
        var failed = Assert.Single(h.Queue.DrainEvents().OfType<ToolInstallFailedEvent>());
        Assert.Equal(ToolCodes.SmokeTestFailed, failed.Reason.Code);
    }

    [Fact]
    public async Task A_manual_rollback_swaps_current_and_previous()
    {
        await using var h = await InstalledAsync("1.0");
        await SetChannelAsync(h, YtDlpChannel.Nightly);
        h.Feed.Publish(ManagedTool.YtDlp, "1.0.5", channel: ToolChannels.Nightly);
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        var item = await WaitingForToolFixAsync(h);
        h.Queue.DrainEvents();

        Assert.Equal(ToolRollbackResult.RolledBack, await h.Manager.RollbackAsync("yt-dlp", Ct));

        Assert.Equal(YtDlpPath(h, "1.0"), h.Manager.Current.YtDlp);
        var row = await h.RowAsync(ManagedTool.YtDlp);
        Assert.Equal(("1.0", "stable", "1.0.5", "nightly", "1.0.5"), (row!.Current, row.Channel, row.Previous, row.PreviousChannel, row.SkippedVersion));
        Assert.Contains(h.Queue.DrainEvents(), e => e is ToolRolledBackEvent { Version: "1.0", FromVersion: "1.0.5", Automatic: false });
        Assert.Equal(ItemState.Queued, (await h.Queue.ItemAsync(item)).State);
        Assert.Equal("1.0.5", (await h.Manager.GetDetailsAsync(Ct)).Tools[0].PreviousVersion);

        // Still on nightly, but the nightly it left is skipped.
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        Assert.Equal(YtDlpPath(h, "1.0"), h.Manager.Current.YtDlp);
    }

    [Fact]
    public async Task A_rollback_needs_a_working_previous_version_of_a_managed_tool()
    {
        await using var h = await InstalledAsync("1.0");

        Assert.Equal(ToolRollbackResult.NoPreviousVersion, await h.Manager.RollbackAsync("deno", Ct));
        Assert.Equal(ToolRollbackResult.UnknownTool, await h.Manager.RollbackAsync("python", Ct));

        h.Feed.Publish(ManagedTool.Deno, "2.0");
        await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);
        await File.WriteAllTextAsync(Path.Combine(h.VersionDir(ManagedTool.Deno, "1.0"), "deno.exe"), "broken", Ct);

        Assert.Equal(ToolRollbackResult.PreviousVersionBroken, await h.Manager.RollbackAsync("deno", Ct));
        Assert.Equal(Path.Combine(h.VersionDir(ManagedTool.Deno, "2.0"), "deno.exe"), h.Manager.Current.Deno);
    }

    [Fact]
    public async Task An_overridden_tool_is_not_updated_or_rolled_back()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"fake-yt-dlp-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(fake, "", Ct);
        try
        {
            await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"), new ToolLocatorOptions { YtDlp = fake });
            await h.Manager.CheckAsync(smoke: true, Ct);
            h.Feed.Publish(ManagedTool.YtDlp, "2.0");

            await h.Manager.CheckForUpdatesAsync(cancellationToken: Ct);

            Assert.Equal(fake, h.Manager.Current.YtDlp);
            Assert.Equal(ToolRollbackResult.NotManaged, await h.Manager.RollbackAsync("yt-dlp", Ct));
            Assert.Equal(new ToolDetails("yt-dlp", null, true, false, null, null, null), (await h.Manager.GetDetailsAsync(Ct)).Tools[0]);
        }
        finally
        {
            File.Delete(fake);
        }
    }

    [Fact]
    public async Task The_loop_checks_for_updates_at_startup_and_then_on_schedule()
    {
        await using var h = await ToolHarness.StartAsync(
            f => f.PublishAll("1.0"),
            options: new ToolManagerOptions { RetryDelays = [TimeSpan.FromMilliseconds(50)], UpdateCheckInterval = TimeSpan.FromMilliseconds(300) });
        await h.Manager.StartAsync(Ct);
        await h.WaitForEventAsync<ToolsCheckedEvent>();
        Assert.True(h.Manager.ToolsReady);

        h.Feed.Publish(ManagedTool.Ffmpeg, "2.0");

        var installed = await h.WaitForEventAsync<ToolInstalledEvent>(e => e.Tool == "ffmpeg");
        Assert.Equal(("2.0", "1.0"), (installed.Version, installed.PreviousVersion));
        await h.WaitForEventAsync<ToolsCheckedEvent>();
    }

    [Fact]
    public async Task A_forced_check_updates_yt_dlp_at_once_and_requeues_the_waiting_items()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.StartAsync(Ct);
        await h.WaitForEventAsync<ToolsCheckedEvent>();
        var item = await WaitingForToolFixAsync(h);
        h.Feed.Publish(ManagedTool.YtDlp, "1.1");
        h.Feed.Publish(ManagedTool.Deno, "1.1");

        h.Manager.RequestYtDlpUpdateCheck();

        var installed = await h.WaitForEventAsync<ToolInstalledEvent>();
        Assert.Equal(("yt-dlp", "1.1"), (installed.Tool, installed.Version));
        await h.WaitForEventAsync<ToolsCheckedEvent>();
        Assert.Equal(ItemState.Queued, (await h.Queue.ItemAsync(item)).State);

        // A forced check is about yt-dlp; Deno waits for the next scheduled check.
        Assert.Equal(Path.Combine(h.VersionDir(ManagedTool.Deno, "1.0"), "deno.exe"), h.Manager.Current.Deno);
    }

    [Fact]
    public async Task A_channel_change_request_installs_the_channels_build()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.StartAsync(Ct);
        await h.WaitForEventAsync<ToolsCheckedEvent>();
        h.Feed.Publish(ManagedTool.YtDlp, "1.0.7", channel: ToolChannels.Nightly);
        await SetChannelAsync(h, YtDlpChannel.Nightly);

        h.Manager.RequestUpdateCheck(ytDlpOnly: true);

        var installed = await h.WaitForEventAsync<ToolInstalledEvent>();
        Assert.Equal(("yt-dlp", "1.0.7"), (installed.Tool, installed.Version));
    }

    private static async Task<ToolHarness> InstalledAsync(string version)
    {
        var h = await ToolHarness.StartAsync(f => f.PublishAll(version));
        Assert.True(await h.Manager.CheckAsync(smoke: true, Ct));
        return h;
    }

    private static async Task<long> WaitingForToolFixAsync(ToolHarness h)
    {
        var item = await h.Queue.AddAsync();
        await h.Queue.Actor.StartResolvingAsync(item.Id, Ct);
        await h.Queue.Actor.FailAsync(item.Id, ToolBroken, Ct);
        Assert.Equal(ItemState.WaitingToolFix, (await h.Queue.ItemAsync(item.Id)).State);
        return item.Id;
    }

    private static async Task SetChannelAsync(ToolHarness h, YtDlpChannel channel)
    {
        var settings = h.Queue.Root.Settings;
        await settings.SaveAsync(await settings.LoadAsync(Ct) with { YtDlpChannel = channel }, Ct);
    }

    private static string YtDlpPath(ToolHarness h, string version) => Path.Combine(h.VersionDir(ManagedTool.YtDlp, version), "yt-dlp.exe");
}
