namespace YoutubeDownloadHelper.FakeYtDlp;

/// <summary>
/// The part of the yt-dlp command line the fake understands. Options that take values must be listed in
/// <see cref="ValueCounts"/> so their values aren't mistaken for URLs; any other option is ignored.
/// </summary>
internal sealed class FakeOptions
{
    private static readonly Dictionary<string, int> ValueCounts = new(StringComparer.Ordinal)
    {
        ["-o"] = 1, ["--output"] = 1, ["-P"] = 1, ["--paths"] = 1, ["--cookies"] = 1,
        ["--progress-template"] = 1, ["--print"] = 1, ["-O"] = 1, ["-f"] = 1, ["--format"] = 1,
        ["-S"] = 1, ["--format-sort"] = 1, ["--playlist-end"] = 1, ["--playlist-start"] = 1, ["-I"] = 1,
        ["--playlist-items"] = 1, ["--cache-dir"] = 1, ["--ffmpeg-location"] = 1, ["--js-runtimes"] = 1,
        ["--encoding"] = 1, ["--progress-delta"] = 1, ["--audio-format"] = 1, ["--audio-quality"] = 1,
        ["--merge-output-format"] = 1, ["--remux-video"] = 1, ["--recode-video"] = 1, ["--limit-rate"] = 1,
        ["-r"] = 1, ["--retries"] = 1, ["--fragment-retries"] = 1, ["--socket-timeout"] = 1,
        ["--sleep-interval"] = 1, ["--max-sleep-interval"] = 1, ["--sleep-requests"] = 1,
        ["--extractor-args"] = 1, ["--user-agent"] = 1, ["--referer"] = 1, ["--add-header"] = 1,
        ["--proxy"] = 1, ["--download-archive"] = 1, ["--exec"] = 1, ["--postprocessor-args"] = 1,
        ["--ppa"] = 1, ["--cookies-from-browser"] = 1, ["--parse-metadata"] = 1, ["--sub-langs"] = 1,
        ["--convert-thumbnails"] = 1, ["--concurrent-fragments"] = 1, ["-N"] = 1, ["--print-to-file"] = 2,
        ["--replace-in-metadata"] = 3, ["--output-na-placeholder"] = 1, ["--config-locations"] = 1,
        ["--batch-file"] = 1, ["-a"] = 1, ["--username"] = 1, ["-u"] = 1, ["--password"] = 1, ["-p"] = 1,
        ["--plugin-dirs"] = 1, ["--remote-components"] = 1, ["--compat-options"] = 1,
        ["--use-extractors"] = 1, ["--default-search"] = 1, ["--match-filters"] = 1,
        ["--break-match-filters"] = 1, ["--http-chunk-size"] = 1, ["--throttled-rate"] = 1,
        ["--wait-for-video"] = 1, ["--xff"] = 1, ["--impersonate"] = 1,
    };

    private static readonly string[] PrintWhens =
        ["video", "pre_process", "after_filter", "before_dl", "post_process", "after_move", "after_video", "playlist"];

    private static readonly string[] OutputTypes =
        ["subtitle", "thumbnail", "description", "annotation", "infojson", "link", "pl_thumbnail", "pl_description",
         "pl_infojson", "chapter", "pl_video"];

    private readonly List<(string Name, string[] Values)> _options = [];

    public List<string> Urls { get; } = [];

    public static FakeOptions Parse(IReadOnlyList<string> args)
    {
        var options = new FakeOptions();
        var onlyPositional = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (onlyPositional || arg.Length < 2 || arg[0] != '-')
            {
                options.Urls.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                onlyPositional = true;
                continue;
            }

            var eq = arg.StartsWith("--", StringComparison.Ordinal) ? arg.IndexOf('=', StringComparison.Ordinal) : -1;
            if (eq > 0)
            {
                options._options.Add((arg[..eq], [arg[(eq + 1)..]]));
                continue;
            }

            var count = ValueCounts.GetValueOrDefault(arg);
            var values = args.Skip(i + 1).Take(count).ToArray();
            i += values.Length;
            options._options.Add((arg, values));
        }

        return options;
    }

    public bool Has(params string[] names) => _options.Any(o => names.Contains(o.Name, StringComparer.Ordinal));

    public IEnumerable<string> All(params string[] names) =>
        _options.Where(o => names.Contains(o.Name, StringComparer.Ordinal) && o.Values.Length > 0).Select(o => o.Values[0]);

    public string? Last(params string[] names) => All(names).LastOrDefault();

    public bool Version => Has("--version");

    public bool DumpJson => Has("-J", "--dump-single-json", "-j", "--dump-json");

    public bool FlatPlaylist => Has("--flat-playlist", "--extract-flat");

    public bool IgnoreNoFormats => Has("--ignore-no-formats-error");

    public int? PlaylistEnd => int.TryParse(Last("--playlist-end"), System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    public IReadOnlyList<(string When, string Template)> Prints =>
        [.. All("--print", "-O").Select(SplitPrint)];

    /// <summary><c>--print</c> implies <c>--quiet</c>, like <c>-J</c> (spike #33).</summary>
    public bool Quiet => Has("-q", "--quiet") || DumpJson || Prints.Count > 0;

    /// <summary>Without <c>--progress</c>, a quiet run shows no progress at all (spike #33).</summary>
    public bool ShowProgress => !Has("--no-progress") && (Has("--progress") || !Quiet);

    public string? DownloadTemplate => Template("download");

    public string? PostprocessTemplate => Template("postprocess");

    public string? OutputTemplate =>
        All("-o", "--output").Where(v => !HasPrefix(v, OutputTypes, out _)).Select(v => StripPrefix(v, "default:")).LastOrDefault();

    public string? HomePath => PathOf("home");

    public string? TempPath => PathOf("temp");

    public string? CookiesPath => Last("--cookies");

    public bool ExtractAudio => Has("-x", "--extract-audio");

    public string? AudioFormat => Last("--audio-format");

    public bool EmbedMetadata => Has("--embed-metadata", "--add-metadata", "--embed-chapters", "--add-chapters");

    public bool EmbedThumbnail => Has("--embed-thumbnail");

    public bool Utf8 => string.Equals(Last("--encoding")?.Replace("-", "", StringComparison.Ordinal), "utf8", StringComparison.OrdinalIgnoreCase);

    private string? Template(string type)
    {
        string? result = null;
        foreach (var value in All("--progress-template"))
        {
            var colon = value.IndexOf(':', StringComparison.Ordinal);
            var valueType = colon > 0 && value[..colon] is "download" or "postprocess" or "download-title" or "postprocess-title"
                ? value[..colon]
                : "download";
            if (valueType == type)
            {
                result = valueType == value[..Math.Max(colon, 0)] ? value[(colon + 1)..] : value;
            }
        }

        return result;
    }

    private string? PathOf(string type)
    {
        string? result = null;
        foreach (var value in All("-P", "--paths"))
        {
            if (value.StartsWith(type + ":", StringComparison.Ordinal))
            {
                result = value[(type.Length + 1)..];
            }
            else if (type == "home" && !HasPrefix(value, ["temp", "home", .. OutputTypes], out _))
            {
                result = value;
            }
        }

        return result;
    }

    private static (string When, string Template) SplitPrint(string value) =>
        HasPrefix(value, PrintWhens, out var when) ? (when, value[(when.Length + 1)..]) : ("video", value);

    private static bool HasPrefix(string value, IEnumerable<string> types, out string type)
    {
        type = types.FirstOrDefault(t => value.StartsWith(t + ":", StringComparison.Ordinal)) ?? "";
        return type.Length > 0;
    }

    private static string StripPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
}
