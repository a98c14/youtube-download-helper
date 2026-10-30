using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace YoutubeDownloadHelper.FakeYtDlp;

/// <summary>One invocation of the fake. The scenario comes from the URL; see the README for the list.</summary>
internal sealed partial class FakeRun(FakeOptions options, FakeRequest request, FakeConsole console, CookieJar? cookies)
{
    public const string DefaultVersion = "2026.10.26";

    /// <summary>Internal: the <c>spawn-child</c> scenario starts the fake again with only this argument.</summary>
    private const string ChildFlag = "--fake-child";

    /// <summary>Reported speed when the URL has no <c>speed</c>; nothing sleeps.</summary>
    private const double NominalSpeed = 10 << 20;

    private static readonly HashSet<string> VideoScenarios = new(StringComparer.Ordinal)
    {
        "success", "slow", "hang", "spawn-child", "resume", "http-429", "http-5xx", "connection-reset",
        "members-only", "premium-only", "age-restricted", "bot-check", "private", "removed", "geo-blocked",
        "extractor-failure", "nsig", "disk-full", "access-denied", "unknown-error", "live", "upcoming",
        "cookie-writeback",
    };

    private bool LoggedIn => cookies?.LoggedIn ?? false;

    public static int Execute(string[] args)
    {
        if (args is [ChildFlag])
        {
            Thread.Sleep(Timeout.Infinite);
        }

        var options = FakeOptions.Parse(args);
        var console = new FakeConsole(options.Utf8);
        if (options.Version)
        {
            console.Out(Environment.GetEnvironmentVariable("FAKE_YTDLP_VERSION") ?? DefaultVersion);
            return 0;
        }

        if (options.Urls.Count == 0)
        {
            console.Err("Usage: yt-dlp [OPTIONS] URL [URL...]");
            console.Err("");
            console.Err("yt-dlp: error: You must provide at least one URL.");
            console.Err("Type yt-dlp --help to see a list of all options.");
            return 2;
        }

        var request = FakeRequest.Parse(options.Urls[0]);
        if (request is null)
        {
            console.Err(YtDlpMessages.Unsupported(options.Urls[0]));
            return 1;
        }

        // yt-dlp saves the cookie jar back on every exit, failures included.
        var cookies = CookieJar.TryLoad(options.CookiesPath);
        try
        {
            return new FakeRun(options, request, console, cookies).Run();
        }
        finally
        {
            cookies?.Save();
        }
    }

    private int Run() => request.Scenario switch
    {
        "playlist" => Listing(tab: null),
        "channel" => Listing(request.Sub ?? "videos"),
        "subscriptions" => Subscriptions(),
        var s when VideoScenarios.Contains(s) => Video(),
        _ => Fail(YtDlpMessages.Unsupported(request.Url)),
    };

    private int Video()
    {
        var scenario = request.Scenario;
        var info = new VideoInfo(
            request.Text("id", DefaultId(scenario)),
            request.Text("title", $"Fake video ({scenario})"),
            request.Text("channel", "Fake Channel"),
            request.Text("upload_date", "20260102"),
            scenario switch
            {
                "members-only" => "subscriber_only",
                "premium-only" => "premium_only",
                "age-restricted" => "needs_auth",
                _ => "public",
            },
            scenario switch
            {
                "live" => "is_live",
                "upcoming" => "is_upcoming",
                _ => "not_live",
            });

        Screen($"[youtube] Extracting URL: {request.Url}");
        Screen($"[youtube] {info.Id}: Downloading webpage");
        if (YtDlpMessages.Extraction(scenario, info.Id, LoggedIn) is { } errors)
        {
            foreach (var line in errors)
            {
                console.Err(line);
            }

            return 1;
        }

        if (scenario == "upcoming" && !(options.DumpJson && options.IgnoreNoFormats))
        {
            return Fail(YtDlpMessages.Upcoming(info.Id));
        }

        if (options.DumpJson)
        {
            console.Out(PyJson.Dumps(Metadata(info)));
            return 0;
        }

        if (scenario == "cookie-writeback")
        {
            cookies?.Rotate(request.Text("rotate", "SIDCC,__Secure-1PSIDCC,__Secure-3PSIDCC").Split(','));
        }

        return Download(info);
    }

    private int Download(VideoInfo info)
    {
        var scenario = request.Scenario;
        var size = request.Bytes("size", scenario == "slow" ? 2 << 20 : 192 << 10);
        var throttled = scenario == "slow" || request.Has("speed");
        var speed = throttled ? Math.Max(1, request.Bytes("speed", 256 << 10)) : NominalSpeed;
        var audio = options.ExtractAudio;
        Format[] formats = audio
            ? [new("251", "webm", size)]
            : [new("399", "mp4", size * 3 / 4), new("140", "m4a", size - (size * 3 / 4))];
        var finalExt = audio ? options.AudioFormat is null or "best" ? "opus" : options.AudioFormat : "mp4";
        var plan = new Plan(info, formats, string.Join('+', formats.Select(f => f.Id)), audio ? "webm" : "mp4", size);

        var relative = OutputTemplate.Render(
            options.OutputTemplate ?? "%(title)s [%(id)s].%(ext)s", Fields(plan, finalExt, null), sanitize: true);
        var home = Path.GetFullPath(options.HomePath ?? Environment.CurrentDirectory);
        var temp = options.TempPath is null ? home : Path.GetFullPath(options.TempPath);
        var finalPath = Path.GetFullPath(Path.Combine(home, relative));
        var workPath = Path.GetFullPath(Path.Combine(temp, relative));
        var workBase = workPath.EndsWith("." + finalExt, StringComparison.Ordinal) ? workPath[..^(finalExt.Length + 1)] : workPath;
        string FileOf(Format f) => audio ? $"{workBase}.{f.Ext}" : $"{workBase}.f{f.Id}.{f.Ext}";

        Screen($"[info] {info.Id}: Downloading 1 format(s): {plan.FormatId}");
        Print("video", plan, finalExt, null);
        Print("before_dl", plan, finalExt, null);

        switch (scenario)
        {
            case "live":
                return Live(info);
            case "http-429":
                return Fail(YtDlpMessages.HttpError(429));
            case "http-5xx":
                return Fail(YtDlpMessages.HttpError((int)request.Number("status", 503)));
        }

        // Spike #33: an existing destination is not downloaded or merged, but the other post-processors run.
        if (File.Exists(finalPath))
        {
            Screen($"[download] {finalPath} has already been downloaded");
            PostProcess(plan, finalExt, finalPath, finalPath, () => { });
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(workBase)!);
        if (scenario == "access-denied")
        {
            return Fail(YtDlpMessages.AccessDenied(FileOf(formats[0]) + ".part"));
        }

        if (scenario == "spawn-child")
        {
            SpawnChild();
        }

        var fresh = formats.All(f => !File.Exists(FileOf(f)) && !File.Exists(FileOf(f) + ".part"));
        double? stopAt = scenario switch
        {
            "hang" or "spawn-child" => request.Number("stop", 0.5),
            "resume" when fresh => request.Number("stop", 0.5),
            "connection-reset" => request.Number("stop", 0.25),
            "disk-full" => request.Number("stop", 0.5),
            _ => null,
        };

        long before = 0;
        foreach (var format in formats)
        {
            var file = FileOf(format);
            if (File.Exists(file))
            {
                // yt-dlp reports an already downloaded format as one "finished" hook with only total_bytes.
                Screen($"[download] {file} has already been downloaded");
                DownloadLine(info, format, new PyDict { { "status", "finished" }, { "total_bytes", new FileInfo(file).Length } });
                before += format.Size;
                continue;
            }

            Screen($"[download] Destination: {file}");
            var part = file + ".part";
            var done = File.Exists(part) ? Math.Min(new FileInfo(part).Length, format.Size) : 0;
            if (done > 0)
            {
                Screen(string.Create(CultureInfo.InvariantCulture, $"[download] Resuming download at byte {done}"));
            }

            var start = done;
            var chunk = throttled ? Math.Max(1, (long)(speed / 4)) : Math.Max(1, (format.Size + 3) / 4);
            using (var stream = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                while (done < format.Size)
                {
                    var next = Math.Min(format.Size, done + chunk);
                    if (throttled)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds((next - done) / speed));
                    }

                    stream.SetLength(next);
                    stream.Flush();
                    done = next;
                    DownloadLine(info, format, new PyDict
                    {
                        { "status", "downloading" },
                        { "downloaded_bytes", done },
                        { "total_bytes", format.Size },
                        { "speed", speed },
                        { "eta", (long)((format.Size - done) / speed) },
                        { "elapsed", (done - start) / speed },
                    });

                    if (stopAt is { } stop && before + done >= stop * size)
                    {
                        return Stop(scenario);
                    }
                }
            }

            DownloadLine(info, format, new PyDict
            {
                { "status", "finished" },
                { "downloaded_bytes", done },
                { "total_bytes", format.Size },
                { "speed", speed },
                { "elapsed", (done - start) / speed },
            });
            File.Move(part, file, overwrite: true);
            before += format.Size;
        }

        var current = $"{workBase}.{finalExt}";
        PostProcess(plan, finalExt, current, finalPath, () =>
        {
            if (audio)
            {
                Pp("ExtractAudio", info, FileOf(formats[0]), $"[ExtractAudio] Destination: {current}",
                    () => File.Move(FileOf(formats[0]), current, overwrite: true));
            }
            else
            {
                Pp("Merger", info, current, $"[Merger] Merging formats into \"{current}\"", () =>
                {
                    using (var merged = new FileStream(current, FileMode.Create, FileAccess.Write))
                    {
                        merged.SetLength(size);
                    }

                    foreach (var format in formats)
                    {
                        File.Delete(FileOf(format));
                    }
                });
            }
        });
        return 0;
    }

    /// <summary>Runs the post-processors after the download (or merge/convert step) and prints <c>after_move</c>.</summary>
    private void PostProcess(Plan plan, string finalExt, string current, string finalPath, Action convert)
    {
        convert();
        if (options.EmbedMetadata)
        {
            Pp("Metadata", plan.Info, current, $"[Metadata] Adding metadata to \"{current}\"", () => { });
        }

        if (options.EmbedThumbnail)
        {
            Pp("EmbedThumbnail", plan.Info, current, null, () => { });
        }

        Pp("MoveFiles", plan.Info, current, null, () =>
        {
            if (!string.Equals(current, finalPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                File.Move(current, finalPath);
            }
        });
        Print("after_move", plan, finalExt, finalPath);
    }

    private int Stop(string scenario)
    {
        switch (scenario)
        {
            case "connection-reset":
                for (var i = 1; i <= 10; i++)
                {
                    Screen(string.Create(CultureInfo.InvariantCulture, $"[download] Got error: {YtDlpMessages.ConnectionReset}. Retrying ({i}/10)..."));
                }

                return Fail($"ERROR: [download] Got error: {YtDlpMessages.ConnectionReset}. Giving up after 10 retries");
            case "disk-full":
                return Fail(YtDlpMessages.DiskFull);
            default:
                Thread.Sleep(Timeout.Infinite);
                return 0;
        }
    }

    /// <summary>A live stream never finishes: fragment progress every 250 ms until the process is killed.</summary>
    private int Live(VideoInfo info)
    {
        var format = new Format("95", "mp4", 0);
        for (var i = 1; ; i++)
        {
            Thread.Sleep(250);
            DownloadLine(info, format, new PyDict
            {
                { "status", "downloading" },
                { "downloaded_bytes", i * 65536L },
                { "speed", 262144.0 },
                { "elapsed", i * 0.25 },
                { "fragment_index", i },
            });
        }
    }

    private void SpawnChild()
    {
        var exe = Environment.ProcessPath!;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(FakeRun).Assembly.Location);
        }

        start.ArgumentList.Add(ChildFlag);
        using var child = Process.Start(start)!;
        console.Out(string.Create(CultureInfo.InvariantCulture, $"[fake] child pid {child.Id}"));
    }

    private void DownloadLine(VideoInfo info, Format format, PyDict progress)
    {
        if (!options.ShowProgress)
        {
            return;
        }

        if (options.DownloadTemplate is not null)
        {
            console.Out("YDH1 {\"k\":\"dl\",\"p\":" + PyJson.Dumps(progress) + ",\"i\":" +
                PyJson.Dumps(new PyDict { { "id", info.Id }, { "format_id", format.Id } }) + "}");
            return;
        }

        var values = progress.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var total = Convert.ToInt64(values.GetValueOrDefault("total_bytes") ?? 0L, CultureInfo.InvariantCulture);
        var speed = Convert.ToDouble(values.GetValueOrDefault("speed") ?? 0.0, CultureInfo.InvariantCulture);
        if (Equals(values["status"], "finished"))
        {
            var elapsed = TimeSpan.FromSeconds(Convert.ToDouble(values.GetValueOrDefault("elapsed") ?? 0.0, CultureInfo.InvariantCulture));
            console.Out(string.Create(CultureInfo.InvariantCulture, $"[download] 100% of {Size(total),10} in {elapsed:hh\\:mm\\:ss} at {Size(speed)}/s"));
            return;
        }

        var downloaded = Convert.ToInt64(values["downloaded_bytes"], CultureInfo.InvariantCulture);
        var percent = total > 0 ? 100.0 * downloaded / total : 0;
        var eta = TimeSpan.FromSeconds(Convert.ToDouble(values.GetValueOrDefault("eta") ?? 0L, CultureInfo.InvariantCulture));
        console.Out(string.Create(CultureInfo.InvariantCulture, $"[download] {percent,5:0.0}% of {Size(total),10} at {Size(speed) + "/s",12} ETA {eta:mm\\:ss}"));
    }

    private void Pp(string name, VideoInfo info, string path, string? screen, Action work)
    {
        PpLine("started", name, info, path);
        if (screen is not null)
        {
            Screen(screen);
        }

        work();
        PpLine("finished", name, info, path);
    }

    private void PpLine(string status, string name, VideoInfo info, string path)
    {
        if (options.ShowProgress && options.PostprocessTemplate is not null)
        {
            console.Err("YDH1 {\"k\":\"pp\",\"p\":" + PyJson.Dumps(new PyDict { { "status", status }, { "postprocessor", name } }) +
                ",\"i\":" + PyJson.Dumps(new PyDict { { "id", info.Id }, { "filepath", path } }) + "}");
        }
    }

    /// <summary>
    /// <c>--print</c>: the spike's <c>plan</c> and <c>moved</c> templates (recognised by their <c>"k"</c>) print the
    /// YDH1 lines; anything else is a field name or an output template.
    /// </summary>
    private void Print(string when, Plan plan, string finalExt, string? filepath)
    {
        foreach (var (printWhen, template) in options.Prints)
        {
            if (printWhen != when)
            {
                continue;
            }

            if (template.Contains("\"k\":\"plan\"", StringComparison.Ordinal))
            {
                console.Out("YDH1 {\"k\":\"plan\",\"i\":" + PyJson.Dumps(new PyDict
                {
                    { "id", plan.Info.Id },
                    { "title", plan.Info.Title },
                    { "channel", plan.Info.Channel },
                    { "format_id", plan.FormatId },
                    { "ext", plan.Ext },
                    { "filesize", plan.Formats.Length == 1 ? plan.Size : null },
                    { "filesize_approx", plan.Size },
                }) + ",\"rf\":" + PyJson.Dumps(plan.Formats.Length == 1 ? [] : plan.Formats.Select(f => new PyDict
                {
                    { "format_id", f.Id },
                    { "ext", f.Ext },
                    { "protocol", "https" },
                    { "filesize", f.Size },
                    { "filesize_approx", f.Size },
                }).ToArray()) + "}");
            }
            else if (template.Contains("\"k\":\"moved\"", StringComparison.Ordinal))
            {
                console.Out("YDH1 {\"k\":\"moved\",\"i\":" + PyJson.Dumps(new PyDict { { "id", plan.Info.Id }, { "filepath", filepath } }) + "}");
            }
            else
            {
                var expanded = FieldName().IsMatch(template) ? $"%({template})s" : template;
                console.Out(OutputTemplate.Render(expanded, Fields(plan, finalExt, filepath), sanitize: false));
            }
        }
    }

    private static Dictionary<string, string> Fields(Plan plan, string ext, string? filepath)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = plan.Info.Id,
            ["title"] = plan.Info.Title,
            ["fulltitle"] = plan.Info.Title,
            ["channel"] = plan.Info.Channel,
            ["uploader"] = plan.Info.Channel,
            ["channel_id"] = VideoInfo.ChannelId,
            ["upload_date"] = plan.Info.UploadDate,
            ["ext"] = ext,
            ["format_id"] = plan.FormatId,
            ["webpage_url"] = $"https://www.youtube.com/watch?v={plan.Info.Id}",
        };
        if (filepath is not null)
        {
            fields["filepath"] = filepath;
            fields["filename"] = filepath;
        }

        return fields;
    }

    private PyDict Metadata(VideoInfo info) => new()
    {
        { "id", info.Id },
        { "title", info.Title },
        { "fulltitle", info.Title },
        { "channel", info.Channel },
        { "channel_id", VideoInfo.ChannelId },
        { "channel_url", $"https://www.youtube.com/channel/{VideoInfo.ChannelId}" },
        { "uploader", info.Channel },
        { "upload_date", info.UploadDate },
        { "duration", 300 },
        { "availability", info.Availability },
        { "live_status", info.LiveStatus },
        { "is_live", info.LiveStatus == "is_live" },
        { "was_live", false },
        { "webpage_url", $"https://www.youtube.com/watch?v={info.Id}" },
        { "original_url", request.Url },
        { "extractor", "youtube" },
        { "extractor_key", "Youtube" },
        { "ext", "mp4" },
        { "format_id", "399+140" },
        { "_type", "video" },
    };

    private void Screen(string line)
    {
        if (!options.Quiet)
        {
            console.Out(line);
        }
    }

    private int Fail(string line)
    {
        console.Err(line);
        return 1;
    }

    /// <summary>An 11-character id derived from the scenario name: <c>success</c> → <c>fksuccess00</c>.</summary>
    private static string DefaultId(string scenario)
    {
        var letters = new string([.. scenario.Where(char.IsAsciiLetterOrDigit)]);
        return ("fk" + letters).PadRight(11, '0')[..11];
    }

    private static string Size(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes:0.00}{units[unit]}");
    }

    [GeneratedRegex(@"^[\w.+-]+$")]
    private static partial Regex FieldName();

    private sealed record VideoInfo(string Id, string Title, string Channel, string UploadDate, string Availability, string LiveStatus)
    {
        public const string ChannelId = "UCfakechannel000000000aa";
    }

    private sealed record Format(string Id, string Ext, long Size);

    private sealed record Plan(VideoInfo Info, Format[] Formats, string FormatId, string Ext, long Size);
}
