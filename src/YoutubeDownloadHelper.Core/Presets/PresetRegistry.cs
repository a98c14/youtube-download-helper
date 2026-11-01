using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using YoutubeDownloadHelper.Contracts.Presets;

namespace YoutubeDownloadHelper.Core.Presets;

/// <summary>
/// The built-in presets (PLAN.md §5.3). The format strings are recorded in the plan and covered by the argument
/// snapshot tests; change both together.
/// </summary>
public sealed class PresetRegistry
{
    /// <summary>Highest resolution up to the limit (the smallest above it if there is none), then H.264 video and AAC audio.</summary>
    private static string[] Video(int maxHeight) =>
        ["-S", string.Create(CultureInfo.InvariantCulture, $"res:{maxHeight},vcodec:h264,acodec:aac"), "--merge-output-format", "mp4"];

    /// <summary>Every preset, in the order clients list them; the first is the default.</summary>
    public static IReadOnlyList<Preset> BuiltIn { get; } =
    [
        // yt-dlp's defaults: bv*+ba/b, its own sort, and its own container (MKV when a WebM merge gets a thumbnail).
        new(PresetIds.Best, PresetCodes.Best, "Best quality", AudioOnly: false, []),
        new(PresetIds.P1080, PresetCodes.P1080, "1080p", AudioOnly: false, Video(1080)),
        new(PresetIds.P720, PresetCodes.P720, "720p", AudioOnly: false, Video(720)),

        // An AAC (m4a) stream is copied, not re-encoded; -x only re-encodes when the video has no m4a audio at all.
        new(PresetIds.AudioM4a, PresetCodes.AudioM4a, "Audio (M4A)", AudioOnly: true,
            ["-f", "ba[ext=m4a]/ba/b", "-x", "--audio-format", "m4a"]),

        // VBR quality 0 (LAME -V0), the best MP3 quality.
        new(PresetIds.AudioMp3, PresetCodes.AudioMp3, "Audio (MP3)", AudioOnly: true,
            ["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0"]),
    ];

    private readonly Dictionary<string, Preset> _byId = BuiltIn.ToDictionary(p => p.Id, StringComparer.Ordinal);

    public IReadOnlyList<Preset> All => BuiltIn;

    public Preset Default => BuiltIn[0];

    public bool Contains(string id) => _byId.ContainsKey(id);

    public bool TryGet(string id, [MaybeNullWhen(false)] out Preset preset) => _byId.TryGetValue(id, out preset);

    /// <exception cref="ArgumentException">The ID is not a known preset.</exception>
    public Preset Get(string id) =>
        _byId.TryGetValue(id, out var preset) ? preset : throw new ArgumentException($"Unknown preset '{id}'.", nameof(id));

    /// <summary>What clients see: IDs and localizable names, never the arguments.</summary>
    public IReadOnlyList<PresetInfo> Describe() => [.. BuiltIn.Select(p => p.ToInfo())];
}
