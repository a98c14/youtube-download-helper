using System.Globalization;
using System.Text.Json;

namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>
/// A video's metadata from <c>yt-dlp -J --no-playlist</c> (PLAN.md §5.1, Resolving). <see cref="Availability"/> and
/// <see cref="LiveStatus"/> are yt-dlp's own values (<c>public</c>, <c>subscriber_only</c>, …; <c>not_live</c>,
/// <c>is_live</c>, …), stored as they are.
/// </summary>
/// <param name="Extractor">yt-dlp's extractor key, lowercase (<c>youtube</c>); with <paramref name="VideoId"/> the archive key (PLAN.md §5.4).</param>
/// <param name="VideoId">The video id.</param>
/// <param name="Title">The title.</param>
/// <param name="Channel">The channel name (yt-dlp's <c>channel</c>, else <c>uploader</c>).</param>
/// <param name="ChannelId">The channel id.</param>
/// <param name="UploadDate">The upload date (<c>upload_date</c>, <c>YYYYMMDD</c>).</param>
/// <param name="Duration">The duration; unknown for a live stream.</param>
/// <param name="Availability">yt-dlp's <c>availability</c>.</param>
/// <param name="LiveStatus">yt-dlp's <c>live_status</c>.</param>
/// <param name="SizeEstimate">The selected formats' <c>filesize</c>, else <c>filesize_approx</c>.</param>
/// <param name="Filename">
/// yt-dlp's predicted path for the file (<c>filename</c>) when the resolve used the download's output options
/// (<see cref="YtDlpCommandBuilder.Resolve(DownloadRequest)"/>). Its extension is the downloaded one: a conversion or
/// remux can still change it, so compare names without the extension (<see cref="DestinationCheck"/>).
/// </param>
public sealed record ResolvedVideo(
    string Extractor,
    string VideoId,
    string? Title,
    string? Channel,
    string? ChannelId,
    DateOnly? UploadDate,
    TimeSpan? Duration,
    string? Availability,
    string? LiveStatus,
    long? SizeEstimate,
    string? Filename)
{
    /// <summary>Maps yt-dlp's JSON. Returns <see langword="null"/> when it isn't a single video with an id.</summary>
    public static ResolvedVideo? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ResolvedVideo? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || Text(root, "_type") is { } type && type != "video"
            || Text(root, "id") is not { Length: > 0 } id)
        {
            return null;
        }

        var extractor = Text(root, "extractor_key") ?? Text(root, "extractor") ?? "generic";
        return new ResolvedVideo(
            extractor.ToLowerInvariant(),
            id,
            Text(root, "title") ?? Text(root, "fulltitle"),
            Text(root, "channel") ?? Text(root, "uploader"),
            Text(root, "channel_id"),
            DateOnly.TryParseExact(Text(root, "upload_date"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null,
            Number(root, "duration") is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : null,
            Text(root, "availability"),
            Text(root, "live_status"),
            Number(root, "filesize") is > 0 and var exact ? (long)exact
                : Number(root, "filesize_approx") is > 0 and var approx ? (long)approx : null,
            Text(root, "filename") ?? Text(root, "_filename"));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)
            && double.IsFinite(d) && d < long.MaxValue
            ? d
            : null;
}
