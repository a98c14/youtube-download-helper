using System.Text.Json;
using YoutubeDownloadHelper.Core.Resolve;

namespace YoutubeDownloadHelper.Core.Tests.Resolve;

public sealed class ResolvedListingTests
{
    // Shaped like `yt-dlp --flat-playlist -J` on a channel URL without a tab, and on a tab.
    private const string ChannelHome = """
        {"id": "UCabc", "channel": "Kanal", "channel_id": "UCabc", "title": "Kanal", "_type": "playlist", "entries": [
          {"_type": "url", "ie_key": "YoutubeTab", "id": "UCabc", "url": "https://www.youtube.com/@kanal/videos", "title": "Kanal - Videos"},
          {"_type": "url", "ie_key": "YoutubeTab", "id": "UCabc", "url": "https://www.youtube.com/@kanal/shorts", "title": "Kanal - Shorts"}
        ], "extractor_key": "YoutubeTab"}
        """;

    private const string Tab = """
        {"id": "UCabc", "uploader": "Kanal", "title": "Kanal - Videos", "_type": "playlist", "playlist_count": 3, "entries": [
          {"_type": "url", "ie_key": "Youtube", "id": "dQw4w9WgXcQ", "url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "title": "İlk", "duration": 212.0, "availability": "subscriber_only", "live_status": null},
          {"_type": "url", "ie_key": "Youtube", "id": "jNQXAC9IVRw", "url": "https://www.youtube.com/shorts/jNQXAC9IVRw", "title": "Kısa", "duration": null, "upload_date": "20260102"},
          {"_type": "url", "ie_key": "Youtube", "id": "noUrl000000", "title": "skipped"},
          null
        ]}
        """;

    [Fact]
    public void A_tab_lists_its_videos()
    {
        var listing = Parse(Tab)!;

        Assert.Equal(("UCabc", "Kanal - Videos", "Kanal", 3), (listing.Id, listing.Title, listing.Channel, listing.PlaylistCount));
        Assert.Equal(2, listing.Entries.Count);
        var first = listing.Entries[0];
        Assert.Equal(
            (false, "youtube", "dQw4w9WgXcQ", "İlk", TimeSpan.FromSeconds(212), "subscriber_only", (string?)null),
            (first.IsPlaylist, first.Extractor, first.Id, first.Title, first.Duration, first.Availability, first.LiveStatus));
        Assert.Equal((null, new DateOnly(2026, 1, 2)), (listing.Entries[1].Duration, listing.Entries[1].UploadDate));
    }

    [Fact]
    public void Tabs_of_a_channel_are_playlist_entries()
    {
        var listing = Parse(ChannelHome)!;

        Assert.All(listing.Entries, e => Assert.True(e.IsPlaylist));
        Assert.Null(listing.PlaylistCount);
        Assert.Equal("https://www.youtube.com/@kanal/shorts", listing.Entries[1].Url);
    }

    [Fact]
    public void A_video_is_not_a_listing()
    {
        Assert.Null(Parse("""{"_type": "video", "id": "dQw4w9WgXcQ"}"""));
        Assert.Null(Parse("""{"id": "dQw4w9WgXcQ"}"""));
    }

    private static ResolvedListing? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ResolvedListing.Parse(document.RootElement);
    }
}
