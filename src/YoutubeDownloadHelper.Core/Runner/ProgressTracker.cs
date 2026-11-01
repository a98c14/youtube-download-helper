using System.Text.Json;

namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>What a line of yt-dlp output was.</summary>
public enum OutputLineKind
{
    /// <summary>Not a <c>YDH1</c> line (or a malformed one): <c>WARNING:</c>, <c>ERROR:</c>, screen output.</summary>
    Raw,

    /// <summary><c>plan</c>: the formats to download, before the download starts.</summary>
    Plan,

    /// <summary><c>dl</c>: download progress. Kept out of the output tail (spike #33).</summary>
    Download,

    /// <summary><c>pp</c>: a post-processor started or finished.</summary>
    PostProcess,

    /// <summary><c>moved</c>: the final path.</summary>
    Moved,
}

/// <summary>
/// Turns the <c>YDH1</c> lines of one download run into a <see cref="DownloadProgress"/> and the final path, following
/// the parsing rules of spike #33 (<c>docs/spikes/progress-and-paths.md</c>): every field is optional, numbers may
/// be ints or floats, fragment downloads use <c>fragment_index / fragment_count</c> (clamped), and a merge weighs
/// its formats by the sizes in the <c>plan</c> line, or equally when they aren't known. Not thread-safe.
/// </summary>
public sealed class ProgressTracker
{
    private const string MoveFiles = "MoveFiles";

    private readonly List<string> _planned = [];
    private readonly Dictionary<string, long> _plannedSizes = new(StringComparer.Ordinal);
    private readonly List<FormatState> _formats = [];
    private string? _postprocessor;
    private double? _speed;
    private double? _lineEta;

    public DownloadPhase Phase { get; private set; } = DownloadPhase.Preparing;

    /// <summary>The final path from the <c>moved</c> line (never a work-folder path from <c>dl</c>/<c>pp</c>).</summary>
    public string? FinalPath { get; private set; }

    /// <summary>
    /// <c>moved</c> arrived without any <c>dl</c> line: yt-dlp found the file already in the destination and only
    /// re-ran the embeds (spike #33, "Existing destination file").
    /// </summary>
    public bool AlreadyPresent => FinalPath is not null && _formats.Count == 0;

    /// <summary>Reads one line (without its line ending) and returns what it was.</summary>
    public OutputLineKind Read(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.StartsWith(YtDlpTemplates.LinePrefix, StringComparison.Ordinal))
        {
            return OutputLineKind.Raw;
        }

        try
        {
            using var json = JsonDocument.Parse(line.AsMemory(YtDlpTemplates.LinePrefix.Length));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return OutputLineKind.Raw;
            }

            switch (Text(root, "k"))
            {
                case "plan":
                    ReadPlan(root);
                    return OutputLineKind.Plan;
                case "dl":
                    ReadDownload(root);
                    return OutputLineKind.Download;
                case "pp":
                    ReadPostProcess(root);
                    return OutputLineKind.PostProcess;
                case "moved":
                    if (Object(root, "i") is { } info && Text(info, "filepath") is { Length: > 0 } path)
                    {
                        FinalPath = path;
                    }

                    Phase = DownloadPhase.Done;
                    return OutputLineKind.Moved;
                default:
                    return OutputLineKind.Raw;
            }
        }
        catch (JsonException)
        {
            return OutputLineKind.Raw;
        }
    }

    /// <summary>The current snapshot.</summary>
    public DownloadProgress Snapshot()
    {
        if (_formats.Count == 0 && _planned.Count == 0)
        {
            return new DownloadProgress(Phase, null, null, null, null, Phase == DownloadPhase.Done ? 1 : null, _postprocessor);
        }

        // The formats yt-dlp will download: the plan's, plus any it reports that the plan didn't list.
        var expected = new List<string>(_planned);
        expected.AddRange(_formats.Select(f => f.Id).Where(id => !_planned.Contains(id, StringComparer.Ordinal)));
        var states = expected.Select(id => _formats.Find(f => f.Id == id)).ToList();

        long downloaded = 0;
        foreach (var state in states)
        {
            downloaded += state is null ? 0 : state.Finished ? state.Total ?? state.Downloaded ?? 0 : state.Downloaded ?? 0;
        }

        // Weights: the planned sizes when every format has one, else equal weight per format.
        var sizes = expected.Select(id => _plannedSizes.TryGetValue(id, out var s) && s > 0 ? s : (long?)null).ToList();
        var bySize = sizes.All(s => s is not null);
        long? total = bySize
            ? sizes.Sum(s => s!.Value)
            : states.All(s => s?.Total is > 0) ? states.Sum(s => s!.Total!.Value) : null;

        double? fraction = 0;
        double weights = 0;
        for (var i = 0; i < expected.Count; i++)
        {
            var weight = bySize ? sizes[i]!.Value : 1.0;
            weights += weight;
            var part = states[i] is { } state ? state.Fraction : 0;
            fraction = part is null ? null : fraction + (weight * part.Value);
        }

        fraction = fraction / weights;
        if (Phase is DownloadPhase.Processing or DownloadPhase.Moving or DownloadPhase.Done)
        {
            fraction = 1;
        }

        TimeSpan? eta = null;
        if (total is { } t && _speed is > 0 && Phase == DownloadPhase.Downloading)
        {
            eta = TimeSpan.FromSeconds(Math.Max(0, (t - downloaded) / _speed.Value));
        }
        else if (_lineEta is { } seconds && Phase == DownloadPhase.Downloading)
        {
            eta = TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        return new DownloadProgress(
            Phase,
            downloaded,
            total,
            Phase == DownloadPhase.Downloading ? _speed : null,
            eta,
            fraction is { } f ? Math.Clamp(f, 0, 1) : null,
            _postprocessor);
    }

    private void ReadPlan(JsonElement root)
    {
        _planned.Clear();
        _plannedSizes.Clear();
        if (Array(root, "rf") is { } requested && requested.GetArrayLength() > 0)
        {
            foreach (var format in requested.EnumerateArray())
            {
                if (format.ValueKind == JsonValueKind.Object && Text(format, "format_id") is { } id)
                {
                    _planned.Add(id);
                    if (Size(format) is { } size)
                    {
                        _plannedSizes[id] = size;
                    }
                }
            }
        }
        else if (Object(root, "i") is { } info && Text(info, "format_id") is { } id)
        {
            _planned.Add(id);
            if (Size(info) is { } size)
            {
                _plannedSizes[id] = size;
            }
        }
    }

    private void ReadDownload(JsonElement root)
    {
        var progress = Object(root, "p");
        if (progress is not { } p)
        {
            return;
        }

        var id = (Object(root, "i") is { } info ? Text(info, "format_id") : null) ?? "";
        var state = _formats.Find(f => f.Id == id);
        if (state is null)
        {
            state = new FormatState(id);
            _formats.Add(state);
        }

        state.Finished = Text(p, "status") == "finished";
        state.Downloaded = Long(p, "downloaded_bytes") ?? state.Downloaded;
        state.Total = Long(p, "total_bytes") ?? Long(p, "total_bytes_estimate") ?? state.Total;
        state.FragmentIndex = Number(p, "fragment_index") ?? state.FragmentIndex;
        state.FragmentCount = Number(p, "fragment_count") ?? state.FragmentCount;
        if (state.Finished && Long(p, "total_bytes") is { } exact)
        {
            state.Downloaded = exact;
        }

        _speed = Number(p, "speed");
        _lineEta = Number(p, "eta");
        if (Phase == DownloadPhase.Preparing)
        {
            Phase = DownloadPhase.Downloading;
        }
    }

    private void ReadPostProcess(JsonElement root)
    {
        if (Object(root, "p") is not { } p || Text(p, "status") != "started")
        {
            return;
        }

        _postprocessor = Text(p, "postprocessor");
        Phase = _postprocessor == MoveFiles ? DownloadPhase.Moving : DownloadPhase.Processing;
    }

    private static long? Size(JsonElement element) =>
        Long(element, "filesize") is > 0 and var exact ? exact : Long(element, "filesize_approx") is > 0 and var approx ? approx : null;

    private static JsonElement? Object(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static JsonElement? Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)
            && double.IsFinite(d)
            ? d
            : null;

    private static long? Long(JsonElement element, string name) =>
        Number(element, name) is { } d && d >= 0 && d < long.MaxValue ? (long)d : null;

    private sealed class FormatState(string id)
    {
        public string Id { get; } = id;

        public bool Finished { get; set; }

        public long? Downloaded { get; set; }

        public long? Total { get; set; }

        public double? FragmentIndex { get; set; }

        public double? FragmentCount { get; set; }

        /// <summary>Fragments when known (estimates grow while they arrive), else bytes; null when neither is known.</summary>
        public double? Fraction =>
            Finished ? 1
            : FragmentCount is > 0 ? Math.Clamp((FragmentIndex ?? 0) / FragmentCount.Value, 0, 1)
            : Total is > 0 ? Math.Clamp((double)(Downloaded ?? 0) / Total.Value, 0, 1)
            : null;
    }
}
