using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Tools;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Tests.Tools;

public sealed class ToolManagerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Installs_every_tool_into_its_version_folder_and_records_it()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("2026.10.26"));
        Assert.False(h.Manager.ToolsReady);

        Assert.True(await h.Manager.CheckAsync(smoke: true, Ct));

        Assert.True(h.Manager.ToolsReady);
        Assert.Equal(
            new ToolPaths(
                Path.Combine(h.Paths.ToolsDir, "yt-dlp", "2026.10.26", "yt-dlp.exe"),
                Path.Combine(h.Paths.ToolsDir, "ffmpeg", "2026.10.26", "ffmpeg.exe"),
                Path.Combine(h.Paths.ToolsDir, "deno", "2026.10.26", "deno.exe")),
            h.Manager.Current);
        Assert.True(File.Exists(Path.Combine(h.VersionDir(ManagedTool.Ffmpeg, "2026.10.26"), "ffprobe.exe")));
        Assert.False(File.Exists(Path.Combine(h.VersionDir(ManagedTool.Ffmpeg, "2026.10.26"), "README.txt")));
        Assert.Equal(
            [new ToolInfo("yt-dlp", "2026.10.26", true), new ToolInfo("ffmpeg", "2026.10.26", true), new ToolInfo("deno", "2026.10.26", true)],
            h.Manager.GetTools());

        var row = await h.RowAsync(ManagedTool.YtDlp);
        Assert.NotNull(row);
        Assert.Equal(("2026.10.26", null, "stable", h.Queue.Root.Time.Now), (row.Current, row.Previous, row.Channel, row.LastCheck));
        Assert.Null((await h.RowAsync(ManagedTool.Deno))!.Channel);
        Assert.Empty(h.StagingLeftovers());

        var events = h.Queue.DrainEvents();
        Assert.Equal(
            [ToolInstallPhase.Downloading, ToolInstallPhase.Verifying, ToolInstallPhase.Extracting, ToolInstallPhase.Testing, ToolInstallPhase.Switching],
            events.OfType<ToolInstallProgressEvent>().Where(e => e.Tool == "yt-dlp").Select(e => e.Phase).Distinct());
        Assert.Contains(events, e => e is ToolInstallProgressEvent { Phase: ToolInstallPhase.Downloading, BytesReceived: > 0, TotalBytes: > 0 });
        Assert.Equal(["yt-dlp", "ffmpeg", "deno"], events.OfType<ToolInstalledEvent>().Select(e => e.Tool));
        Assert.Equal([false, true], events.OfType<ToolHealthChangedEvent>().Select(e => e.ToolsReady));
    }

    [Fact]
    public async Task A_healthy_install_is_kept_and_survives_a_restart()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.CheckAsync(smoke: true, Ct);
        h.Feed.PublishAll("2.0");

        var manager = await h.RestartAsync();
        Assert.True(manager.ToolsReady);
        Assert.True(await manager.CheckAsync(smoke: true, Ct));

        Assert.Equal(3, h.Feed.Downloads);
        Assert.Equal(Path.Combine(h.VersionDir(ManagedTool.YtDlp, "1.0"), "yt-dlp.exe"), manager.Current.YtDlp);
    }

    [Fact]
    public async Task A_new_version_becomes_current_and_the_old_one_previous()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.CheckAsync(smoke: true, Ct);
        h.Feed.Publish(ManagedTool.YtDlp, "2.0");
        h.Queue.DrainEvents();

        Assert.True(await h.Manager.InstallLatestAsync("yt-dlp", Ct));

        Assert.Equal(Path.Combine(h.VersionDir(ManagedTool.YtDlp, "2.0"), "yt-dlp.exe"), h.Manager.Current.YtDlp);
        Assert.True(Directory.Exists(h.VersionDir(ManagedTool.YtDlp, "1.0")), "The old version was touched.");
        var row = await h.RowAsync(ManagedTool.YtDlp);
        Assert.Equal(("2.0", "1.0"), (row!.Current, row.Previous));
        Assert.Contains(h.Queue.DrainEvents(), e => e is ToolInstalledEvent { Tool: "yt-dlp", Version: "2.0", PreviousVersion: "1.0" });
    }

    [Fact]
    public async Task A_checksum_mismatch_is_rejected_and_nothing_is_switched()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.CheckAsync(smoke: true, Ct);
        h.Feed.Publish(ManagedTool.YtDlp, "2.0").CorruptChecksum();
        h.Queue.DrainEvents();
        var smokeRuns = h.Smoke.Runs;

        Assert.False(await h.Manager.InstallLatestAsync("yt-dlp", Ct));

        await AssertStillOnAsync(h, "1.0", rejected: "2.0");
        var failed = Assert.Single(h.Queue.DrainEvents().OfType<ToolInstallFailedEvent>());
        Assert.Equal(("yt-dlp", ToolCodes.ChecksumMismatch, "yt-dlp"), (failed.Tool, failed.Reason.Code, failed.Reason.Args["tool"]));
        Assert.Equal(smokeRuns, h.Smoke.Runs);
    }

    [Fact]
    public async Task A_release_without_a_checksum_for_the_asset_is_rejected()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.CheckAsync(smoke: true, Ct);
        h.Feed.Publish(ManagedTool.Deno, "2.0").ChecksumText = "0000  something-else.zip\n";
        h.Queue.DrainEvents();

        Assert.False(await h.Manager.InstallLatestAsync("deno", Ct));

        Assert.Equal(Path.Combine(h.VersionDir(ManagedTool.Deno, "1.0"), "deno.exe"), h.Manager.Current.Deno);
        Assert.False(Directory.Exists(h.VersionDir(ManagedTool.Deno, "2.0")));
        Assert.Equal(ToolCodes.ChecksumMissing, Assert.Single(h.Queue.DrainEvents().OfType<ToolInstallFailedEvent>()).Reason.Code);
    }

    [Fact]
    public async Task A_first_install_with_a_bad_checksum_leaves_the_tools_missing()
    {
        await using var h = await ToolHarness.StartAsync(f =>
        {
            f.PublishAll("1.0");
            f.Publish(ManagedTool.Ffmpeg, "1.0").CorruptChecksum();
        });

        Assert.False(await h.Manager.CheckAsync(smoke: true, Ct));

        Assert.False(h.Manager.ToolsReady);
        Assert.False(Directory.Exists(h.VersionDir(ManagedTool.Ffmpeg, "1.0")));
        Assert.Null(await h.RowAsync(ManagedTool.Ffmpeg));
        Assert.Equal([true, false, true], h.Manager.GetTools().Select(t => t.Installed));
    }

    [Fact]
    public async Task A_failed_smoke_test_leaves_the_current_version_unchanged()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.CheckAsync(smoke: true, Ct);
        h.Feed.Publish(ManagedTool.YtDlp, "2.0", content: "broken");
        h.Queue.DrainEvents();

        Assert.False(await h.Manager.InstallLatestAsync("yt-dlp", Ct));

        await AssertStillOnAsync(h, "1.0", rejected: "2.0");
        var failed = Assert.Single(h.Queue.DrainEvents().OfType<ToolInstallFailedEvent>());
        Assert.Equal((ToolCodes.SmokeTestFailed, "yt-dlp.exe"), (failed.Reason.Code, failed.Reason.Args["file"]));
        Assert.True(h.Manager.ToolsReady);
    }

    [Fact]
    public async Task A_download_that_breaks_off_leaves_no_half_installed_version()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.CheckAsync(smoke: true, Ct);
        h.Feed.Publish(ManagedTool.YtDlp, "2.0").BreakHalfWay = true;

        Assert.False(await h.Manager.InstallLatestAsync("yt-dlp", Ct));

        await AssertStillOnAsync(h, "1.0", rejected: "2.0");
    }

    [Fact]
    public async Task A_cancelled_install_leaves_no_half_installed_version()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.CheckAsync(smoke: true, Ct);
        var release = h.Feed.Publish(ManagedTool.YtDlp, "2.0");
        release.Hold = new TaskCompletionSource();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var install = h.Manager.InstallLatestAsync("yt-dlp", cts.Token);
        await release.HalfWay.Task.WaitAsync(Ct);
        Assert.NotEmpty(h.StagingLeftovers());
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        await AssertStillOnAsync(h, "1.0", rejected: "2.0");
    }

    [Fact]
    public async Task What_an_interrupted_install_left_is_cleaned_up_and_never_current()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.CheckAsync(smoke: true, Ct);

        // As if the engine died mid-install: a staged download, and a finished version folder whose switch never committed.
        var staged = Directory.CreateDirectory(Path.Combine(h.Installer.StagingDir, "yt-dlp-dead", "content"));
        await File.WriteAllTextAsync(Path.Combine(staged.FullName, "yt-dlp.exe"), "ok half", Ct);
        Directory.CreateDirectory(h.VersionDir(ManagedTool.YtDlp, "2.0"));
        await File.WriteAllTextAsync(Path.Combine(h.VersionDir(ManagedTool.YtDlp, "2.0"), "yt-dlp.exe"), "ok yt-dlp 2.0", Ct);

        var manager = await h.RestartAsync();

        Assert.Empty(h.StagingLeftovers());
        Assert.Equal(Path.Combine(h.VersionDir(ManagedTool.YtDlp, "1.0"), "yt-dlp.exe"), manager.Current.YtDlp);
    }

    [Fact]
    public async Task A_version_folder_that_is_already_complete_is_reused_not_replaced()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.CheckAsync(smoke: true, Ct);
        var dir = Directory.CreateDirectory(h.VersionDir(ManagedTool.YtDlp, "2.0")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "yt-dlp.exe"), "ok from an earlier install", Ct);
        h.Feed.Publish(ManagedTool.YtDlp, "2.0");

        Assert.True(await h.Manager.InstallLatestAsync("yt-dlp", Ct));

        Assert.Equal("ok from an earlier install", await File.ReadAllTextAsync(h.Manager.Current.YtDlp, Ct));
        Assert.Empty(h.StagingLeftovers());
    }

    [Fact]
    public async Task A_deleted_binary_raises_tools_missing_and_is_reinstalled()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));

        // The startup check installs everything; its last step is reporting the tools ready.
        await h.Manager.StartAsync(Ct);
        await h.WaitForEventAsync<ToolHealthChangedEvent>(e => e.ToolsReady);
        var ffprobe = Path.Combine(h.VersionDir(ManagedTool.Ffmpeg, "1.0"), "ffprobe.exe");

        File.Delete(ffprobe);

        Assert.False(h.Manager.ToolsReady);
        Assert.Contains(h.Manager.GetTools(), t => t is { Tool: "ffmpeg", Installed: false });
        await h.WaitForEventAsync<ToolHealthChangedEvent>(e => !e.ToolsReady);
        var installed = await h.WaitForEventAsync<ToolInstalledEvent>(e => e.Tool == "ffmpeg");
        await h.WaitForEventAsync<ToolHealthChangedEvent>(e => e.ToolsReady);

        Assert.Equal(("ffmpeg", "1.0"), (installed.Tool, installed.Version));
        Assert.True(File.Exists(ffprobe));
        Assert.True(h.Manager.ToolsReady);
        Assert.Empty(h.StagingLeftovers());
    }

    [Fact]
    public async Task A_broken_binary_found_by_the_startup_check_is_reinstalled()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        await h.Manager.CheckAsync(smoke: true, Ct);
        await File.WriteAllTextAsync(h.Manager.Current.Deno, "corrupted", Ct);
        var manager = await h.RestartAsync();

        Assert.True(await manager.CheckAsync(smoke: true, Ct));

        Assert.StartsWith("ok", await File.ReadAllTextAsync(manager.Current.Deno, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_at_first_start_retries_until_the_tools_are_installed()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        h.Feed.Offline = true;
        await h.Manager.StartAsync(Ct);
        await h.WaitForEventAsync<ToolInstallFailedEvent>(e => e.Reason.Code == ToolCodes.DownloadFailed);
        Assert.False(h.Manager.ToolsReady);

        h.Feed.Offline = false;

        await h.WaitForEventAsync<ToolHealthChangedEvent>(e => e.ToolsReady);
        Assert.True(h.Manager.ToolsReady);
    }

    [Fact]
    public async Task A_repair_requeues_the_items_waiting_for_a_tool_fix()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));
        var item = await h.Queue.AddAsync();
        await h.Queue.Actor.StartResolvingAsync(item.Id, Ct);
        await h.Queue.Actor.FailAsync(item.Id, new JobFailure(ItemTrigger.WaitForToolFix, ErrorCategory.ToolBroken, new EngineMessage("error.crashed", "x")), Ct);

        await h.Manager.CheckAsync(smoke: true, Ct);

        Assert.Equal(ItemState.Queued, (await h.Queue.ItemAsync(item.Id)).State);
    }

    [Fact]
    public async Task An_update_check_request_smoke_tests_and_repairs_yt_dlp()
    {
        await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"));

        // The startup check installs everything; its last step is reporting the tools ready.
        await h.Manager.StartAsync(Ct);
        await h.WaitForEventAsync<ToolHealthChangedEvent>(e => e.ToolsReady);
        await File.WriteAllTextAsync(h.Manager.Current.YtDlp, "broken", Ct);

        h.Manager.RequestYtDlpUpdateCheck();

        var installed = await h.WaitForEventAsync<ToolInstalledEvent>(e => e.Tool == "yt-dlp");
        Assert.Equal("1.0", installed.Version);
        Assert.StartsWith("ok", await File.ReadAllTextAsync(h.Manager.Current.YtDlp, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_overridden_tool_is_not_managed()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"fake-yt-dlp-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(fake, "", Ct);
        try
        {
            await using var h = await ToolHarness.StartAsync(f => f.PublishAll("1.0"), new ToolLocatorOptions { YtDlp = fake });

            Assert.True(await h.Manager.CheckAsync(smoke: true, Ct));

            Assert.Equal(fake, h.Manager.Current.YtDlp);
            Assert.Equal(2, h.Feed.Downloads);
            Assert.Equal(new ToolInfo("yt-dlp", null, true), h.Manager.GetTools()[0]);
            Assert.Null(await h.RowAsync(ManagedTool.YtDlp));
        }
        finally
        {
            File.Delete(fake);
        }
    }

    [Fact]
    public void Versions_must_be_plain_folder_names()
    {
        Assert.True(ToolInstaller.IsValidVersion("2026.10.26"));
        Assert.True(ToolInstaller.IsValidVersion("2026-11-01-1909"));
        Assert.False(ToolInstaller.IsValidVersion(".."));
        Assert.False(ToolInstaller.IsValidVersion(@"..\evil"));
        Assert.False(ToolInstaller.IsValidVersion("a/b"));
        Assert.False(ToolInstaller.IsValidVersion(""));
        Assert.False(ToolInstaller.IsValidVersion(null));
    }

    private static async Task AssertStillOnAsync(ToolHarness h, string current, string rejected)
    {
        Assert.Equal(Path.Combine(h.VersionDir(ManagedTool.YtDlp, current), "yt-dlp.exe"), h.Manager.Current.YtDlp);
        Assert.False(Directory.Exists(h.VersionDir(ManagedTool.YtDlp, rejected)), "The rejected version has a folder.");
        var row = await h.RowAsync(ManagedTool.YtDlp);
        Assert.Equal((current, null), (row!.Current, row.Previous));
        Assert.Empty(h.StagingLeftovers());
        Assert.True(h.Manager.ToolsReady);
    }
}
