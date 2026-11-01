using System.Globalization;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Contracts.Resolve;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Presets;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Resolve;
using YoutubeDownloadHelper.Core.Scheduling;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Endpoints;

/// <summary>
/// <c>POST /items</c>: validates the inputs, expands playlists when asked, drops duplicates (in the request, in the
/// queue, in the download archive) unless forced, and queues the rest. Playlists are listed before the adds; the adds
/// are serialized, so two requests cannot both add the same video.
/// </summary>
public sealed class ItemAdder(
    QueueActor queue,
    DuplicateFinder duplicates,
    UrlResolver resolver,
    SettingsStore settingsStore,
    PresetRegistry presets) : IDisposable
{
    /// <summary>The most URLs and video IDs one request may give, and the most items it may add after expanding playlists.</summary>
    public const int MaxInputs = ResolveLimits.MaxEntries;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The per-input results, or a 400 message when the request as a whole is invalid.</summary>
    public async Task<(AddItemsResponse? Response, EngineMessage? Invalid)> AddAsync(AddItemsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var urls = request.Urls ?? [];
        var videoIds = request.VideoIds ?? [];
        if (urls.Count + videoIds.Count == 0)
        {
            return (null, ApiErrors.InvalidParameter("urls", "Give at least one URL or video ID."));
        }

        if (urls.Count + videoIds.Count > MaxInputs)
        {
            return (null, ApiErrors.InvalidParameter("urls", $"At most {MaxInputs.ToString(CultureInfo.InvariantCulture)} URLs and video IDs per request."));
        }

        if (request.PresetId is { } presetId && !presets.Contains(presetId))
        {
            return (null, ApiErrors.InvalidParameter("presetId", $"Unknown preset '{presetId}'."));
        }

        if (request.Folder is { } folder && (string.IsNullOrWhiteSpace(folder) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0))
        {
            return (null, ApiErrors.InvalidParameter("folder", "The folder is not a valid path."));
        }

        if (!Enum.IsDefined(request.ExpandPlaylists))
        {
            return (null, ApiErrors.InvalidParameter("expandPlaylists", "Expand playlists: none or all."));
        }

        var candidates = new List<Candidate>();
        for (var i = 0; i < urls.Count; i++)
        {
            var candidate = FromUrl(urls[i]);
            if (request.ExpandPlaylists == PlaylistExpansion.All && candidate.Url is { } url && candidate.VideoId is null)
            {
                // The budget keeps room for the inputs still to come, so the total never exceeds MaxInputs.
                var budget = MaxInputs - candidates.Count - (urls.Count - i - 1) - videoIds.Count;
                candidates.AddRange(await ExpandAsync(candidate.Input, url, budget, cancellationToken));
            }
            else
            {
                candidates.Add(candidate);
            }
        }

        foreach (var input in videoIds)
        {
            candidates.Add(FromVideoId(input));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (new AddItemsResponse(await AddCandidatesAsync(candidates, request, cancellationToken)), null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// The videos of a URL, in order: a single video stays one candidate; a playlist becomes its entries; a channel URL
    /// without a tab becomes the videos of each tab (one level deep). All or nothing: a failure, an empty playlist or one
    /// larger than <paramref name="budget"/> is one result for the input.
    /// </summary>
    private async Task<List<Candidate>> ExpandAsync(string input, string url, int budget, CancellationToken cancellationToken)
    {
        if (budget < 1)
        {
            return [TooLarge(input)];
        }

        var resolution = await resolver.ResolveAsync(url, budget, cancellationToken);
        if (resolution.Failure is not null)
        {
            return [Failed(input, resolution.Error!)];
        }

        if (resolution.Video is { } video)
        {
            var videoId = ResolveEndpoints.YouTubeVideoId(video.Extractor, video.VideoId);
            return [new Candidate(input, url, videoId, null, Metadata: new ItemMetadata(
                video.Extractor, video.VideoId, video.Title, video.Channel, video.ChannelId, video.UploadDate,
                video.Duration?.TotalSeconds, video.Availability, video.LiveStatus, video.SizeEstimate))];
        }

        if (resolution.Truncated)
        {
            return [TooLarge(input)];
        }

        var expanded = new List<Candidate>();
        foreach (var entry in resolution.Listing!.Entries)
        {
            if (!entry.IsPlaylist)
            {
                expanded.Add(FromEntry(entry, input));
                continue;
            }

            if (expanded.Count >= budget)
            {
                return [TooLarge(input)];
            }

            var tab = await resolver.ResolveAsync(entry.Url, budget - expanded.Count, cancellationToken);
            if (tab.Failure is not null)
            {
                return [Failed(input, tab.Error!)];
            }

            if (tab.Truncated)
            {
                return [TooLarge(input)];
            }

            // A playlist inside a tab (e.g. a channel's playlists tab) is not expanded further.
            expanded.AddRange(tab.Listing?.Entries.Where(e => !e.IsPlaylist).Select(e => FromEntry(e, input)) ?? []);
        }

        if (expanded.Count > budget)
        {
            return [TooLarge(input)];
        }

        return expanded.Count > 0
            ? expanded
            : [new Candidate(input, null, null, InputMessage(ItemCodes.PlaylistEmpty, input, "The playlist has no videos."))];
    }

    private async Task<List<AddItemResult>> AddCandidatesAsync(List<Candidate> candidates, AddItemsRequest request, CancellationToken cancellationToken)
    {
        var presetId = request.PresetId;
        if (presetId is null)
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            presetId = presets.Contains(settings.DefaultPresetId) ? settings.DefaultPresetId : presets.Default.Id;
        }

        var index = await duplicates.LoadAsync(
            candidates.Where(c => c.VideoId is not null).Select(c => c.VideoId!),
            cancellationToken);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<AddItemResult>();
        foreach (var candidate in candidates)
        {
            if (candidate.Invalid is { } invalid)
            {
                results.Add(new AddItemResult(
                    candidate.Input, candidate.Failed ? AddItemOutcome.Failed : AddItemOutcome.Invalid, null, invalid, candidate.Playlist));
                continue;
            }

            var key = candidate.VideoId is { } videoId ? "v:" + videoId : "u:" + candidate.Url;
            if (!seen.Add(key))
            {
                results.Add(Duplicate(candidate, ItemCodes.DuplicateInRequest, null, "It is listed more than once."));
                continue;
            }

            if (!request.Force && index.Find(candidate.Url!, candidate.VideoId) is { } duplicate)
            {
                results.Add(duplicate.Kind == EntryDuplicate.Queued
                    ? Duplicate(candidate, ItemCodes.AlreadyQueued, duplicate.ItemId, "It is already in the queue.")
                    : Duplicate(candidate, ItemCodes.AlreadyDownloaded, duplicate.ItemId, "It was already downloaded."));
                continue;
            }

            var metadata = candidate.Metadata
                ?? (candidate.VideoId is null ? null : new ItemMetadata(Extractor: YouTubeUrls.Extractor, VideoId: candidate.VideoId));
            var added = await queue.AddAsync(
                new AddItemRequest(candidate.Url!, presetId, request.Folder, request.Force, request.Paused, Metadata: metadata),
                CancellationToken.None);
            results.Add(new AddItemResult(candidate.Input, AddItemOutcome.Added, added.Id, null, candidate.Playlist));
        }

        return results;
    }

    private static Candidate FromUrl(string? input)
    {
        if (ResolveEndpoints.HttpUrl(input) is not { } url)
        {
            return new Candidate(input ?? string.Empty, null, null, InputMessage(ItemCodes.InvalidUrl, input, "Not an http or https URL."));
        }

        return new Candidate(input!, url, YouTubeUrls.TryGetVideoId(url), null);
    }

    private static Candidate FromVideoId(string? input)
    {
        var id = input?.Trim();
        return YouTubeUrls.IsVideoId(id)
            ? new Candidate(input!, YouTubeUrls.WatchUrl(id!), id, null)
            : new Candidate(input ?? string.Empty, null, null, InputMessage(ItemCodes.InvalidVideoId, input, "Not a YouTube video ID."));
    }

    private static Candidate FromEntry(ListingEntry entry, string playlist) => new(
        entry.Url,
        entry.Url,
        ResolveEndpoints.YouTubeVideoId(entry.Extractor, entry.Id),
        null,
        Playlist: playlist,
        Metadata: new ItemMetadata(
            entry.Id is null ? null : entry.Extractor,
            entry.Id,
            entry.Title,
            entry.Channel,
            entry.ChannelId,
            entry.UploadDate,
            entry.Duration?.TotalSeconds,
            entry.Availability,
            entry.LiveStatus));

    private static Candidate Failed(string input, EngineMessage error) => new(input, null, null, error, Failed: true);

    private static Candidate TooLarge(string input) => new(
        input,
        null,
        null,
        new EngineMessage(
            ItemCodes.PlaylistTooLarge,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["input"] = input,
                ["limit"] = MaxInputs.ToString(CultureInfo.InvariantCulture),
            },
            $"The playlist has more videos than one request may add ({MaxInputs.ToString(CultureInfo.InvariantCulture)})."));

    private static AddItemResult Duplicate(Candidate candidate, string code, long? itemId, string message)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["input"] = candidate.Input };
        if (itemId is { } id)
        {
            args["itemId"] = id.ToString(CultureInfo.InvariantCulture);
        }

        return new AddItemResult(candidate.Input, AddItemOutcome.Duplicate, itemId, new EngineMessage(code, args, message), candidate.Playlist);
    }

    private static EngineMessage InputMessage(string code, string? input, string message) =>
        new(code, new Dictionary<string, string>(StringComparer.Ordinal) { ["input"] = input ?? string.Empty }, message);

    /// <summary>
    /// One video to add: <see cref="Url"/>, or why it is <see cref="Invalid"/> (<see cref="Failed"/>: a playlist
    /// expansion failed). <see cref="Playlist"/> is the input it was expanded from.
    /// </summary>
    private sealed record Candidate(
        string Input,
        string? Url,
        string? VideoId,
        EngineMessage? Invalid,
        bool Failed = false,
        string? Playlist = null,
        ItemMetadata? Metadata = null);
}
