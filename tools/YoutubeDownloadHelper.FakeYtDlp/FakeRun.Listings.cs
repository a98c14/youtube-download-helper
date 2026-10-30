using System.Globalization;

namespace YoutubeDownloadHelper.FakeYtDlp;

internal sealed partial class FakeRun
{
    /// <summary>
    /// <c>--flat-playlist -J</c> for <c>playlist</c> and the channel tabs (<c>channel/videos</c>, <c>/shorts</c>,
    /// <c>/streams</c>, <c>/members</c>). Entries are newest first and numbered <c>count</c> down to 1, so raising
    /// <c>count</c> between two polls adds new uploads at the top.
    /// </summary>
    private int Listing(string? tab)
    {
        var channel = request.Text("channel", "Fake Channel");
        if (tab == "members" && !LoggedIn)
        {
            return Fail(YtDlpMessages.PlaylistDoesNotExist("UUMO" + VideoInfo.ChannelId[2..]));
        }

        var letter = tab switch
        {
            null => 'p',
            "shorts" => 's',
            "streams" => 'l',
            "members" => 'm',
            _ => 'v',
        };
        var result = tab is null
            ? new PyDict { { "id", request.Text("id", "PLfakeplaylist") }, { "title", request.Text("title", "Fake playlist") } }
            : new PyDict { { "id", VideoInfo.ChannelId }, { "title", $"{channel} - {char.ToUpperInvariant(tab[0])}{tab[1..]}" } };
        return PrintListing(result, channel, letter, tab switch
        {
            "streams" => "was_live",
            _ => null,
        }, tab == "members" ? "subscriber_only" : null, playlistCount: tab is null);
    }

    /// <summary>
    /// The auth verification: a flat fetch of the subscriptions feed succeeds only with a logged-in cookie jar.
    /// </summary>
    private int Subscriptions() => LoggedIn
        ? PrintListing(new PyDict { { "id", "subscriptions" }, { "title", "subscriptions" } }, "Fake Channel", 'f', null, null, playlistCount: false)
        : Fail(YtDlpMessages.LoginRequired);

    private int PrintListing(PyDict head, string channel, char letter, string? liveStatus, string? availability, bool playlistCount)
    {
        if (!options.DumpJson)
        {
            return Fail("ERROR: [fake] Only -J listings are simulated for playlists, channels and feeds.");
        }

        var count = Math.Max(0, (int)request.Number("count", 5));
        var prefix = request.Text("prefix", "fk" + letter);
        var entryScenario = request.Text("entry", "success");
        var entries = Enumerable.Range(1, count).Reverse().Take(options.PlaylistEnd ?? count).Select(n =>
        {
            var id = prefix + n.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, 11 - prefix.Length), '0');
            return new PyDict
            {
                { "_type", "url" },
                { "ie_key", "Youtube" },
                { "id", id },
                { "url", $"https://fake.test/{entryScenario}?id={id}" },
                { "title", string.Create(CultureInfo.InvariantCulture, $"Fake entry {n}") },
                { "duration", letter == 's' ? 30.0 : 300.0 },
                { "channel", channel },
                { "channel_id", VideoInfo.ChannelId },
                { "live_status", liveStatus },
                { "availability", availability },
            };
        }).ToArray();

        foreach (var (key, value) in new PyDict
        {
            { "channel", channel },
            { "channel_id", VideoInfo.ChannelId },
            { "uploader", channel },
            { "webpage_url", request.Url },
            { "original_url", request.Url },
            { "_type", "playlist" },
            { "entries", entries },
            { "extractor", "youtube:tab" },
            { "extractor_key", "YoutubeTab" },
            { "playlist_count", playlistCount ? count : null },
        })
        {
            head.Add(key, value);
        }

        console.Out(PyJson.Dumps(head));
        return 0;
    }
}
