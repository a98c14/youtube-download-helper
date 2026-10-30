using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace YoutubeDownloadHelper.Core.Tests.FakeYtDlp;

/// <summary>One smoke test per scenario of <c>tools/YoutubeDownloadHelper.FakeYtDlp</c> (see its README).</summary>
public sealed class FakeYtDlpTests : IDisposable
{
    private const string LoggedInJar =
        "# Netscape HTTP Cookie File\n" +
        ".youtube.com\tTRUE\t/\tTRUE\t1893456000\tLOGIN_INFO\tlogin-value\n" +
        "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1893456000\tSIDCC\tsidcc-value\n" +
        ".youtube.com\tTRUE\t/\tTRUE\t1893456000\tPREF\thl=tr\n";

    private const string LoggedOutJar =
        "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t1893456000\tPREF\thl=tr\n";

    private readonly FakeYtDlpFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Version_prints_the_yt_dlp_version()
    {
        var result = await FakeYtDlpProcess.RunAsync("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["2026.10.26"], result.Stdout);
    }

    [Fact]
    public async Task Success_reports_plan_progress_post_processing_and_the_final_path()
    {
        var result = await RunEngineAsync("https://fake.test/success?title=%C4%B0zmir%20%C4%B1l%C4%B1k%20%F0%9F%98%80");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(result.Stderr, l => !l.StartsWith("YDH1 ", StringComparison.Ordinal));
        Assert.All(result.Stdout.Concat(result.Stderr), l => Assert.DoesNotContain('\r', l));
        Assert.All(result.Lines("dl").Concat(result.Lines("pp")), l => Assert.True(Encoding.ASCII.GetByteCount(l) == l.Length));

        var plan = Json(Assert.Single(result.Lines("plan")));
        Assert.Equal("İzmir ılık 😀", plan.GetProperty("i").GetProperty("title").GetString());
        Assert.Equal("399+140", plan.GetProperty("i").GetProperty("format_id").GetString());
        Assert.Equal(2, plan.GetProperty("rf").GetArrayLength());

        var finished = result.Lines("dl").Select(Json).Where(d => d.GetProperty("p").GetProperty("status").GetString() == "finished");
        Assert.Equal(["399", "140"], finished.Select(d => d.GetProperty("i").GetProperty("format_id").GetString()));
        Assert.Equal(
            ["Merger", "Merger", "Metadata", "Metadata", "EmbedThumbnail", "EmbedThumbnail", "MoveFiles", "MoveFiles"],
            result.Lines("pp").Select(l => Json(l).GetProperty("p").GetProperty("postprocessor").GetString()));
        Assert.All(result.Lines("pp"), l => Assert.Contains(l, result.Stderr));

        var moved = Json(Assert.Single(result.Lines("moved"))).GetProperty("i").GetProperty("filepath").GetString()!;
        Assert.Equal(Path.Combine(_folder.Home, "Fake Channel", "2026-01-02 - İzmir ılık 😀.mp4"), moved);
        Assert.Equal(192 << 10, new FileInfo(moved).Length);
        Assert.Empty(Directory.GetFiles(_folder.Temp, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Success_with_an_existing_destination_skips_download_and_merge()
    {
        await RunEngineAsync("https://fake.test/success");

        var result = await RunEngineAsync("https://fake.test/success");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Lines("dl"));
        Assert.DoesNotContain(result.Lines("pp"), l => l.Contains("Merger", StringComparison.Ordinal));
        Assert.Single(result.Lines("moved"));
    }

    [Fact]
    public async Task Audio_extraction_converts_a_single_format()
    {
        var result = await RunEngineAsync("https://fake.test/success", "-f", "bestaudio", "-x", "--audio-format", "mp3");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[]", Json(Assert.Single(result.Lines("plan"))).GetProperty("rf").GetRawText());
        Assert.Contains(result.Lines("pp"), l => l.Contains("\"ExtractAudio\"", StringComparison.Ordinal));
        var moved = Json(Assert.Single(result.Lines("moved"))).GetProperty("i").GetProperty("filepath").GetString()!;
        Assert.EndsWith(".mp3", moved, StringComparison.Ordinal);
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public async Task Print_after_move_filepath_prints_the_plain_path()
    {
        var result = await FakeYtDlpProcess.RunAsync(
            "--print", "after_move:filepath", "-P", _folder.Home, "--encoding", "utf-8", "https://fake.test/success?title=%C3%A7");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([Path.Combine(_folder.Home, "ç [fksuccess00].mp4")], result.Stdout);
    }

    [Fact]
    public async Task Print_without_progress_is_quiet()
    {
        var result = await FakeYtDlpProcess.RunAsync(
            "--print", "after_move:filepath", "--progress-template", "download:YDH1 {\"k\":\"dl\"}", "-P", _folder.Home,
            "https://fake.test/success");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Lines("dl"));
        Assert.Single(result.Stdout);
    }

    [Fact]
    public async Task Without_encoding_utf8_raw_output_drops_characters_cp1252_lacks()
    {
        var result = await FakeYtDlpProcess.RunAsync(
            ["--print", "title", "-P", _folder.Home, "https://fake.test/success?title=%C4%B0zmir%20%C3%A7%20%F0%9F%98%80"],
            Encoding.Latin1);

        Assert.Equal(["zmir ç "], result.Stdout);
    }

    [Fact]
    public async Task Unknown_flags_are_ignored()
    {
        var result = await RunEngineAsync("https://fake.test/success", "--some-future-flag", "--js-runtimes", "deno:C:\\x\\deno.exe");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Youtube_watch_urls_map_to_success()
    {
        var result = await FakeYtDlpProcess.RunAsync("-J", "https://www.youtube.com/watch?v=jNQXAC9IVRw");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("jNQXAC9IVRw", Json(Assert.Single(result.Stdout)).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Unsupported_url_fails()
    {
        var result = await FakeYtDlpProcess.RunAsync("https://example.com/video");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["ERROR: Unsupported URL: https://example.com/video"], result.Stderr);
    }

    [Fact]
    public async Task Missing_url_is_a_usage_error()
    {
        var result = await FakeYtDlpProcess.RunAsync("--ignore-config");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("yt-dlp: error: You must provide at least one URL.", result.Stderr);
    }

    [Fact]
    public async Task Slow_takes_time_according_to_size_and_speed()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await RunEngineAsync("https://fake.test/slow?size=128K&speed=256K");

        Assert.Equal(0, result.ExitCode);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(0.4), $"Took {stopwatch.Elapsed}.");
        Assert.True(result.Lines("dl").Count() >= 4);
    }

    [Fact]
    public async Task Hang_never_exits()
    {
        using var process = FakeYtDlpProcess.Start(EngineArgs("https://fake.test/hang"));
        try
        {
            await FakeYtDlpProcess.ReadUntilAsync(process, l => l.StartsWith("YDH1 {\"k\":\"dl\"", StringComparison.Ordinal));

            Assert.False(process.WaitForExit(500));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Spawn_child_starts_a_child_that_dies_with_the_tree()
    {
        using var process = FakeYtDlpProcess.Start(EngineArgs("https://fake.test/spawn-child"));
        try
        {
            var line = await FakeYtDlpProcess.ReadUntilAsync(process, l => l.StartsWith("[fake] child pid ", StringComparison.Ordinal));
            using var child = Process.GetProcessById(int.Parse(line["[fake] child pid ".Length..], System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(child.HasExited);

            process.Kill(entireProcessTree: true);

            Assert.True(child.WaitForExit(10_000));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Resume_continues_from_the_part_file_left_by_a_killed_run()
    {
        using (var first = FakeYtDlpProcess.Start(EngineArgs("https://fake.test/resume")))
        {
            await FakeYtDlpProcess.ReadUntilAsync(first, l => l.Contains("\"downloaded_bytes\": 110592", StringComparison.Ordinal));
            Assert.False(first.WaitForExit(200));
            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        var part = Assert.Single(Directory.GetFiles(_folder.Temp, "*.part", SearchOption.AllDirectories));
        Assert.Equal(110592, new FileInfo(part).Length);

        var result = await RunEngineAsync("https://fake.test/resume");

        Assert.Equal(0, result.ExitCode);
        var firstLine = Json(result.Lines("dl").First()).GetProperty("p");
        Assert.Equal(147456, firstLine.GetProperty("downloaded_bytes").GetInt64());
        Assert.Single(result.Lines("moved"));
    }

    [Fact]
    public async Task Resume_prints_the_resume_notice_in_text_mode()
    {
        Directory.CreateDirectory(_folder.Home);
        await File.WriteAllBytesAsync(
            Path.Combine(_folder.Home, "Fake video (resume) [fkresume000].f399.mp4.part"), new byte[1000], TestContext.Current.CancellationToken);

        var result = await FakeYtDlpProcess.RunAsync("-P", _folder.Home, "--newline", "https://fake.test/resume");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[download] Resuming download at byte 1000", result.Stdout);
    }

    [Theory]
    [InlineData("http-429", "ERROR: unable to download video data: HTTP Error 429: Too Many Requests")]
    [InlineData("http-5xx", "ERROR: unable to download video data: HTTP Error 503: Service Unavailable")]
    [InlineData("http-5xx?status=502", "ERROR: unable to download video data: HTTP Error 502: Bad Gateway")]
    [InlineData("connection-reset", "ConnectionResetError(10054, 'An existing connection was forcibly closed by the remote host'")]
    [InlineData("members-only", "Join this channel to get access to members-only content")]
    [InlineData("premium-only", "This video is only available to Music Premium members")]
    [InlineData("age-restricted", "Sign in to confirm your age.")]
    [InlineData("bot-check", "Sign in to confirm you\u2019re not a bot.")]
    [InlineData("private", "Private video. Sign in if you've been granted access to this video.")]
    [InlineData("removed", "Video unavailable. This video has been removed by the uploader")]
    [InlineData("geo-blocked", "The uploader has not made this video available in your country.")]
    [InlineData("extractor-failure", "Unable to extract initial player response; please report this issue on")]
    [InlineData("nsig", "Requested format is not available.")]
    [InlineData("disk-full", "ERROR: unable to write data: [Errno 28] No space left on device")]
    [InlineData("access-denied", "ERROR: unable to open for writing: [Errno 13] Permission denied: 'C:\\\\")]
    [InlineData("unknown-error", "ERROR: [youtube] fkunknowner: ")]
    [InlineData("upcoming", "This live event will begin in a few moments.")]
    public async Task Error_scenarios_exit_1_with_the_yt_dlp_error(string scenario, string expected)
    {
        var result = await RunEngineAsync("https://fake.test/" + scenario);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Lines("moved"));
        var error = result.Stderr[^1];
        Assert.StartsWith("ERROR: ", error, StringComparison.Ordinal);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nsig_warns_before_the_error()
    {
        var result = await RunEngineAsync("https://fake.test/nsig");

        Assert.StartsWith("WARNING: [youtube] fknsig00000: nsig extraction failed", result.Stderr[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("connection-reset")]
    [InlineData("disk-full")]
    public async Task Mid_download_failures_leave_a_part_file(string scenario)
    {
        var result = await RunEngineAsync("https://fake.test/" + scenario);

        Assert.Equal(1, result.ExitCode);
        Assert.NotEmpty(result.Lines("dl"));
        Assert.NotEmpty(Directory.GetFiles(_folder.Temp, "*.part", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("members-only")]
    [InlineData("age-restricted")]
    [InlineData("premium-only")]
    public async Task Auth_scenarios_succeed_when_logged_in(string scenario)
    {
        var jar = WriteJar(LoggedInJar);

        var result = await RunEngineAsync("https://fake.test/" + scenario, "--cookies", jar);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Lines("moved"));
    }

    [Theory]
    [InlineData("success", false, "public", "not_live")]
    [InlineData("members-only", true, "subscriber_only", "not_live")]
    [InlineData("premium-only", true, "premium_only", "not_live")]
    [InlineData("live", false, "public", "is_live")]
    [InlineData("upcoming", false, "public", "is_upcoming")]
    public async Task Dump_json_reports_availability_and_live_status(string scenario, bool loggedIn, string availability, string liveStatus)
    {
        string[] args = ["--ignore-config", "-J", "--no-playlist", "--ignore-no-formats-error", "https://fake.test/" + scenario];
        if (loggedIn)
        {
            args = ["--cookies", WriteJar(LoggedInJar), .. args];
        }

        var result = await FakeYtDlpProcess.RunAsync(args);

        Assert.Equal(0, result.ExitCode);
        var json = Json(Assert.Single(result.Stdout));
        Assert.Equal(availability, json.GetProperty("availability").GetString());
        Assert.Equal(liveStatus, json.GetProperty("live_status").GetString());
        Assert.Equal("video", json.GetProperty("_type").GetString());
    }

    [Fact]
    public async Task Upcoming_without_ignore_no_formats_error_fails_even_for_dump_json()
    {
        var result = await FakeYtDlpProcess.RunAsync("-J", "https://fake.test/upcoming");

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Live_download_streams_fragments_until_killed()
    {
        using var process = FakeYtDlpProcess.Start(EngineArgs("https://fake.test/live"));
        try
        {
            var line = await FakeYtDlpProcess.ReadUntilAsync(process, l => l.Contains("\"fragment_index\": 2", StringComparison.Ordinal));

            Assert.DoesNotContain("total_bytes", line, StringComparison.Ordinal);
            Assert.False(process.HasExited);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Playlist_flat_listing_honours_playlist_end()
    {
        var result = await FakeYtDlpProcess.RunAsync(
            "--flat-playlist", "-J", "--playlist-end", "3", "https://fake.test/playlist?count=10&entry=members-only");

        Assert.Equal(0, result.ExitCode);
        var json = Json(Assert.Single(result.Stdout));
        Assert.Equal("playlist", json.GetProperty("_type").GetString());
        Assert.Equal(10, json.GetProperty("playlist_count").GetInt32());
        var entries = json.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(["fkp00000010", "fkp00000009", "fkp00000008"], entries.Select(e => e.GetProperty("id").GetString()));
        Assert.Equal("https://fake.test/members-only?id=fkp00000010", entries[0].GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("videos", "fkv00000005", null, null)]
    [InlineData("shorts", "fks00000005", null, null)]
    [InlineData("streams", "fkl00000005", "was_live", null)]
    [InlineData("members", "fkm00000005", null, "subscriber_only")]
    public async Task Channel_tabs_list_entries(string tab, string firstId, string? liveStatus, string? availability)
    {
        var result = await FakeYtDlpProcess.RunAsync(
            "--cookies", WriteJar(LoggedInJar), "--flat-playlist", "-J", "--playlist-end", "30", "https://fake.test/channel/" + tab);

        Assert.Equal(0, result.ExitCode);
        var entries = Json(Assert.Single(result.Stdout)).GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(5, entries.Length);
        Assert.Equal(firstId, entries[0].GetProperty("id").GetString());
        Assert.Equal(liveStatus, entries[0].TryGetProperty("live_status", out var l) ? l.GetString() : null);
        Assert.Equal(availability, entries[0].TryGetProperty("availability", out var a) ? a.GetString() : null);
    }

    [Fact]
    public async Task Members_tab_fails_when_logged_out()
    {
        var result = await FakeYtDlpProcess.RunAsync("--flat-playlist", "-J", "https://fake.test/channel/members");

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("ERROR: [youtube:tab] UUMO", Assert.Single(result.Stderr), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cookie_writeback_rotates_sidcc_and_drops_httponly()
    {
        var jar = WriteJar(LoggedInJar);

        var result = await RunEngineAsync("https://fake.test/cookie-writeback", "--cookies", jar);

        Assert.Equal(0, result.ExitCode);
        var saved = await File.ReadAllLinesAsync(jar, TestContext.Current.CancellationToken);
        Assert.Equal("# Netscape HTTP Cookie File", saved[0]);
        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t1893456000\tSIDCC\tsidcc-value-rotated", saved);
        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t1893456000\tLOGIN_INFO\tlogin-value", saved);
        Assert.DoesNotContain(saved, l => l.StartsWith("#HttpOnly_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Other_scenarios_save_the_jar_without_rotating()
    {
        var jar = WriteJar(LoggedInJar);

        await RunEngineAsync("https://fake.test/removed", "--cookies", jar);

        Assert.Contains(".youtube.com\tTRUE\t/\tTRUE\t1893456000\tSIDCC\tsidcc-value", await File.ReadAllLinesAsync(jar, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://www.youtube.com/feed/subscriptions")]
    [InlineData("https://fake.test/subscriptions")]
    public async Task Subscriptions_feed_succeeds_when_logged_in(string url)
    {
        var result = await FakeYtDlpProcess.RunAsync("--cookies", WriteJar(LoggedInJar), "--flat-playlist", "-J", "--playlist-end", "1", url);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(Json(Assert.Single(result.Stdout)).GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task Subscriptions_feed_fails_when_logged_out()
    {
        var result = await FakeYtDlpProcess.RunAsync(
            "--cookies", WriteJar(LoggedOutJar), "--flat-playlist", "-J", "https://www.youtube.com/feed/subscriptions");

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("ERROR: [youtube:tab] Login details are needed to download this content.", Assert.Single(result.Stderr), StringComparison.Ordinal);
    }

    private Task<FakeYtDlpResult> RunEngineAsync(string url, params string[] extra) =>
        FakeYtDlpProcess.RunAsync(EngineArgs(url, extra));

    private string[] EngineArgs(string url, params string[] extra) =>
        [.. FakeYtDlpProcess.EngineArgs(_folder.Temp, _folder.Home), .. extra, url];

    private string WriteJar(string content)
    {
        var path = _folder.In("cookies.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static JsonElement Json(string line) =>
        JsonDocument.Parse(line.StartsWith("YDH1 ", StringComparison.Ordinal) ? line[5..] : line).RootElement;
}
