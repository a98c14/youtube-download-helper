using System.Globalization;
using System.Text.Json;

namespace YoutubeDownloadHelper.Core.Resolve;

/// <summary>
/// A playlist or channel tab from <c>yt-dlp --flat-playlist -J</c> (PLAN.md §5.4): its own fields and its entries,
/// none of them resolved one by one.
/// </summary>
/// <param name="Id">The playlist or channel id.</param>
/// <param name="Title">The playlist or tab title.</param>
/// <param name="Channel">The channel name (<c>channel</c>, else <c>uploader</c>).</param>
/// <param name="ChannelId">The channel id.</param>
/// <param name="PlaylistCount">yt-dlp's <c>playlist_count</c>: the full length, when it knows it (not for channel tabs).</param>
/// <param name="Entries">The entries in yt-dlp's order (newest first for channel tabs).</param>
public sealed record ResolvedListing(
    string? Id,
    string? Title,
    string? Channel,
    string? ChannelId,
    int? PlaylistCount,
    IReadOnlyList<ListingEntry> Entries)
{
    /// <summary>
    /// Maps yt-dlp's JSON. Returns <see langword="null"/> when it isn't a playlist (<c>_type</c> <c>playlist</c>).
    /// Entries without a URL or id are left out.
    /// </summary>
    public static ResolvedListing? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || Json.Text(root, "_type") != "playlist")
        {
            return null;
        }

        var entries = new List<ListingEntry>();
        if (root.TryGetProperty("entries", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (ListingEntry.Parse(element) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return new ResolvedListing(
            Json.Text(root, "id"),
            Json.Text(root, "title"),
            Json.Text(root, "channel") ?? Json.Text(root, "uploader"),
            Json.Text(root, "channel_id"),
            Json.Number(root, "playlist_count") is >= 0 and < int.MaxValue and var count ? (int)count : null,
            entries);
    }
}

/// <summary>One entry of a <see cref="ResolvedListing"/>.</summary>
/// <param name="IsPlaylist">
/// The entry is itself a playlist: a channel tab of a channel URL without a tab, or a playlist of a channel's
/// playlists tab. Resolve its <paramref name="Url"/> to list it.
/// </param>
/// <param name="Url">The URL to queue (a video) or to resolve (a playlist).</param>
/// <param name="Extractor">The entry's extractor key (<c>ie_key</c>), lowercase, e.g. <c>youtube</c>.</param>
/// <param name="Id">The video or playlist id.</param>
/// <param name="Title">The title.</param>
/// <param name="Channel">The channel name.</param>
/// <param name="ChannelId">The channel id.</param>
/// <param name="UploadDate">The upload date, when the listing has it.</param>
/// <param name="Duration">The duration, when the listing has it.</param>
/// <param name="Availability">yt-dlp's <c>availability</c>, when the listing has it (e.g. <c>subscriber_only</c> on a members tab).</param>
/// <param name="LiveStatus">yt-dlp's <c>live_status</c>, when the listing has it.</param>
public sealed record ListingEntry(
    bool IsPlaylist,
    string Url,
    string? Extractor,
    string? Id,
    string? Title,
    string? Channel,
    string? ChannelId,
    DateOnly? UploadDate,
    TimeSpan? Duration,
    string? Availability,
    string? LiveStatus)
{
    public static ListingEntry? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var url = Json.Text(element, "url") ?? Json.Text(element, "webpage_url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var type = Json.Text(element, "_type");
        var extractor = Json.Text(element, "ie_key") ?? Json.Text(element, "extractor_key");
        var isPlaylist = type == "playlist"
            || (extractor is not null
                && (extractor.EndsWith("Tab", StringComparison.Ordinal) || extractor.EndsWith("Playlist", StringComparison.Ordinal)));
        return new ListingEntry(
            isPlaylist,
            url,
            extractor?.ToLowerInvariant(),
            Json.Text(element, "id"),
            Json.Text(element, "title"),
            Json.Text(element, "channel") ?? Json.Text(element, "uploader"),
            Json.Text(element, "channel_id"),
            DateOnly.TryParseExact(Json.Text(element, "upload_date"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null,
            Json.Number(element, "duration") is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : null,
            Json.Text(element, "availability"),
            Json.Text(element, "live_status"));
    }
}

internal static class Json
{
    public static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)
            && double.IsFinite(d) && d < long.MaxValue
            ? d
            : null;
}
