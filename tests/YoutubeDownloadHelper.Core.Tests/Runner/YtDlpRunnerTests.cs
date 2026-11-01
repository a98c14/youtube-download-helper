using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using YoutubeDownloadHelper.Contracts.Presets;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Presets;
using YoutubeDownloadHelper.Core.Runner;
using YoutubeDownloadHelper.Core.Tests.FakeYtDlp;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Tests.Runner;

/// <summary>The runner against <c>YoutubeDownloadHelper.FakeYtDlp</c>, with non-ASCII titles and folders throughout.</summary>
public sealed class YtDlpRunnerTests : IDisposable
{
    private const string Title = "İzmir ılık 😀 東京";
    private const string TitleQuery = "title=%C4%B0zmir%20%C4%B1l%C4%B1k%20%F0%9F%98%80%20%E6%9D%B1%E4%BA%AC";

    private readonly FakeYtDlpFolder _folder = new();
    private readonly DataPaths _paths;
    private readonly YtDlpCommandBuilder _builder;
    private readonly YtDlpRunner _runner = new(TimeProvider.System, NullLogger<YtDlpRunner>.Instance);

    public YtDlpRunnerTests()
    {
        _paths = new DataPaths(_folder.In("data"));
        _builder = new YtDlpCommandBuilder(
            _paths,
            new ConfiguredToolLocator(_paths, new ToolLocatorOptions { YtDlp = FakeYtDlpProcess.ExePath }),
            new PresetRegistry());
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task A_download_reports_progress_and_each_post_processing_phase_and_the_final_path()
    {
        var updates = new List<(TimeSpan At, DownloadProgress Progress)>();
        var clock = Stopwatch.StartNew();
        var request = Request($"https://fake.test/success?{TitleQuery}&size=512K&speed=512K&ppdelay=0.3");

        var result = await _runner.RunAsync(_builder.Download(request), Options(p => updates.Add((clock.Elapsed, p))), Token);

        Assert.True(result.Succeeded, string.Join('\n', result.OutputTail));
        Assert.False(result.Cancelled);
        Assert.False(result.AlreadyPresent);
        var expected = Path.Combine(_folder.Home, "Fake Channel", $"2026-01-02 - {Title}.mp4");
        Assert.Equal(expected, result.FinalPath);
        Assert.True(File.Exists(expected));

        var phases = updates.Select(u => u.Progress.Phase).Distinct().ToList();
        Assert.Equal([DownloadPhase.Downloading, DownloadPhase.Processing, DownloadPhase.Moving], phases.Take(3));
        Assert.Equal(
            ["Merger", "Metadata", "EmbedThumbnail", "MoveFiles"],
            updates.Select(u => u.Progress.Postprocessor).OfType<string>().Distinct());

        var downloading = updates.Select(u => u.Progress).Where(p => p.Phase == DownloadPhase.Downloading).ToList();
        Assert.True(downloading.Count >= 2, $"{downloading.Count} download updates.");
        Assert.All(downloading, p => Assert.Equal(512 * 1024, p.TotalBytes));
        Assert.All(downloading.Zip(downloading.Skip(1)), p => Assert.True(p.Second.Fraction >= p.First.Fraction));
        AssertThrottled(updates.Select(u => u.At).ToList());

        Assert.Equal(DownloadPhase.Done, result.LastProgress.Phase);
        Assert.Equal(1.0, result.LastProgress.Fraction);
    }

    [Fact]
    public async Task Output_written_all_at_once_is_still_throttled()
    {
        var updates = new List<TimeSpan>();
        var clock = Stopwatch.StartNew();

        // Unthrottled, the fake writes every progress and post-processing line within a few milliseconds.
        var result = await _runner.RunAsync(
            _builder.Download(Request("https://fake.test/success?size=64M")), Options(_ => updates.Add(clock.Elapsed)), Token);

        Assert.True(result.Succeeded);
        Assert.NotEmpty(updates);
        AssertThrottled(updates);
    }

    [Fact]
    public async Task The_output_tail_has_no_download_lines_and_the_job_log_has_everything()
    {
        var log = _paths.JobLogFor(7, 1);
        var request = Request($"https://fake.test/success?{TitleQuery}");

        var resolve = await _runner.ResolveAsync(_builder.Resolve(request), new YtDlpRunOptions { LogFile = log }, Token);
        var result = await _runner.RunAsync(_builder.Download(request), new YtDlpRunOptions { LogFile = log }, Token);

        Assert.True(resolve.Run.Succeeded);
        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.OutputTail, l => l.StartsWith("YDH1 {\"k\":\"dl\"", StringComparison.Ordinal));
        Assert.Contains(result.OutputTail, l => l.StartsWith("YDH1 {\"k\":\"pp\"", StringComparison.Ordinal));
        Assert.Contains(result.OutputTail, l => l.StartsWith("YDH1 {\"k\":\"moved\"", StringComparison.Ordinal));

        // One log per attempt: the resolve and the download are both in it.
        var lines = await File.ReadAllLinesAsync(log, Token);
        var headers = lines.Where(l => l.StartsWith("# ", StringComparison.Ordinal) && l.Contains(FakeYtDlpProcess.ExePath, StringComparison.Ordinal)).ToList();
        Assert.Collection(
            headers,
            h => Assert.Contains(" -J ", h, StringComparison.Ordinal),
            h => Assert.Contains(" --progress ", h, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("YDH1 {\"k\":\"dl\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("# stdout: ", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count(l => l == "# exit code 0"));
        Assert.Equal(Path.Combine(_paths.JobLogsDir, "7-1.log"), log);
    }

    [Fact]
    public async Task Raw_output_is_read_as_utf8_and_error_lines_are_kept()
    {
        var result = await _runner.RunAsync(_builder.Download(Request($"https://fake.test/access-denied?{TitleQuery}")), cancellationToken: Token);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        var error = Assert.Single(result.ErrorLines);
        Assert.StartsWith("ERROR: unable to open for writing: [Errno 13] Permission denied: ", error, StringComparison.Ordinal);
        Assert.Contains(Title, error, StringComparison.Ordinal);
        Assert.Contains("Kullanıcı İ 😀", error, StringComparison.Ordinal);
        Assert.Contains(error, result.OutputTail);
        Assert.Null(result.FinalPath);
    }

    [Fact]
    public async Task Warnings_are_error_lines_too()
    {
        var result = await _runner.RunAsync(_builder.Download(Request("https://fake.test/nsig")), cancellationToken: Token);

        Assert.False(result.Succeeded);
        Assert.Collection(
            result.ErrorLines,
            l => Assert.StartsWith("WARNING: [youtube] fknsig00000: nsig extraction failed", l, StringComparison.Ordinal),
            l => Assert.StartsWith("ERROR: [youtube] fknsig00000: Requested format is not available", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_a_hung_download_kills_yt_dlp_and_its_child()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options(p =>
        {
            if (p.Phase == DownloadPhase.Downloading)
            {
                downloading.TrySetResult();
            }
        });

        var run = _runner.RunAsync(_builder.Download(Request("https://fake.test/spawn-child")), options, cancel.Token);
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        await Task.Delay(300, Token);
        Assert.False(run.IsCompleted);

        await cancel.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        var pidLine = Assert.Single(result.OutputTail, l => l.StartsWith("[fake] child pid ", StringComparison.Ordinal));
        AssertExited(int.Parse(pidLine["[fake] child pid ".Length..], CultureInfo.InvariantCulture));
        Assert.Equal(DownloadPhase.Downloading, result.LastProgress.Phase);
        Assert.InRange(result.LastProgress.Fraction!.Value, 0.5, 0.75);
    }

    [Fact]
    public async Task Cancelling_before_the_start_throws()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runner.RunAsync(_builder.Download(Request("https://fake.test/success")), cancellationToken: cancel.Token));
    }

    [Fact]
    public async Task A_missing_executable_throws()
    {
        var command = new YtDlpCommand(_folder.In("missing.exe"), ["--version"]);

        await Assert.ThrowsAsync<Win32Exception>(() => _runner.RunAsync(command, cancellationToken: Token));
    }

    [Theory]
    [InlineData("success", false, "public")]
    [InlineData("members-only", true, "subscriber_only")]
    [InlineData("premium-only", true, "premium_only")]
    [InlineData("age-restricted", true, "needs_auth")]
    public async Task A_resolve_maps_the_metadata_for_each_availability(string scenario, bool loggedIn, string availability)
    {
        var request = Request($"https://fake.test/{scenario}?id=abcdefghijk&{TitleQuery}&size=3M") with
        {
            CookiesFile = loggedIn ? await LoggedInCookiesAsync() : null,
        };

        var result = await _runner.ResolveAsync(_builder.Resolve(request), cancellationToken: Token);

        Assert.True(result.Run.Succeeded, string.Join('\n', result.Run.OutputTail));
        Assert.Equal(
            new ResolvedVideo(
                "youtube",
                "abcdefghijk",
                Title,
                "Fake Channel",
                "UCfakechannel000000000aa",
                new DateOnly(2026, 1, 2),
                TimeSpan.FromMinutes(5),
                availability,
                "not_live",
                3 << 20,
                Path.Combine(_folder.Home, "Fake Channel", $"2026-01-02 - {Title}.mp4")),
            result.Video);
    }

    [Theory]
    [InlineData("live", "is_live")]
    [InlineData("success", "not_live")]
    public async Task A_resolve_reports_the_live_status(string scenario, string liveStatus)
    {
        var result = await _runner.ResolveAsync(_builder.Resolve($"https://fake.test/{scenario}"), cancellationToken: Token);

        Assert.Equal(liveStatus, result.Video?.LiveStatus);
    }

    [Fact]
    public async Task A_failed_resolve_has_no_video_and_keeps_the_error()
    {
        var result = await _runner.ResolveAsync(_builder.Resolve("https://fake.test/private"), cancellationToken: Token);

        Assert.False(result.Run.Succeeded);
        Assert.Null(result.Video);
        Assert.Contains("Private video", Assert.Single(result.Run.ErrorLines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_taken_name_is_detected_before_the_download_and_the_video_id_avoids_it()
    {
        var first = Request($"https://fake.test/success?id=aaaaaaaaaaa&{TitleQuery}");
        Assert.True((await _runner.RunAsync(_builder.Download(first), cancellationToken: Token)).Succeeded);

        // A different video with the same channel, date and title.
        var second = Request($"https://fake.test/success?id=bbbbbbbbbbb&{TitleQuery}") with { PresetId = PresetIds.AudioMp3 };
        var resolved = (await _runner.ResolveAsync(_builder.Resolve(second), cancellationToken: Token)).Video;
        Assert.NotNull(resolved?.Filename);
        Assert.EndsWith(".webm", resolved.Filename, StringComparison.Ordinal);
        Assert.True(DestinationCheck.NameIsTaken(resolved.Filename));

        var result = await _runner.RunAsync(_builder.Download(second with { AppendVideoId = true }), cancellationToken: Token);

        Assert.False(result.AlreadyPresent);
        Assert.Equal(Path.Combine(_folder.Home, "Fake Channel", $"2026-01-02 - {Title} [bbbbbbbbbbb].mp3"), result.FinalPath);
        Assert.False(DestinationCheck.NameIsTaken(Path.Combine(_folder.Home, "Fake Channel", "2026-01-02 - other.mp4")));
        Assert.False(DestinationCheck.NameIsTaken(Path.Combine(_folder.Home, "Missing", "x.mp4")));
    }

    [Fact]
    public async Task Running_into_an_existing_file_is_reported_as_already_present()
    {
        var request = Request("https://fake.test/success");
        Assert.False((await _runner.RunAsync(_builder.Download(request), cancellationToken: Token)).AlreadyPresent);

        var again = await _runner.RunAsync(_builder.Download(request), cancellationToken: Token);

        Assert.True(again.Succeeded);
        Assert.True(again.AlreadyPresent);
    }

    [Fact]
    public async Task A_json_run_returns_all_of_stdout()
    {
        var result = await _runner.RunJsonAsync(_builder.FlatListing("https://fake.test/channel/videos?count=40"), cancellationToken: Token);

        Assert.True(result.Run.Succeeded);
        Assert.Equal(YtDlpCommandBuilder.DefaultListingEnd, System.Text.Json.JsonDocument.Parse(result.Json).RootElement.GetProperty("entries").GetArrayLength());
        Assert.Empty(result.Run.OutputTail);
    }

    /// <summary>
    /// No two updates less than the interval apart (a little slack for the timer), so never more than 4 per second.
    /// </summary>
    private static void AssertThrottled(IReadOnlyList<TimeSpan> times)
    {
        var slack = TimeSpan.FromMilliseconds(20);
        for (var i = 1; i < times.Count; i++)
        {
            Assert.True(times[i] - times[i - 1] >= ProgressThrottle<DownloadProgress>.DefaultInterval - slack, $"Updates {i - 1} and {i}: {times[i] - times[i - 1]}.");
        }
    }

    private static void AssertExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.WaitForExit(10_000), $"Process {pid} is still running.");
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private static YtDlpRunOptions Options(Action<DownloadProgress> onProgress) => new()
    {
        Progress = p =>
        {
            lock (onProgress)
            {
                onProgress(p);
            }
        },
    };

    private DownloadRequest Request(string url) => new()
    {
        ItemId = 7,
        Url = url,
        PresetId = PresetIds.Best,
        DownloadFolder = _folder.Home,
    };

    private async Task<string> LoggedInCookiesAsync()
    {
        var path = _folder.In("cookies.txt");
        await File.WriteAllTextAsync(path, "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t1893456000\tLOGIN_INFO\tlogin\n", Token);
        return path;
    }
}
