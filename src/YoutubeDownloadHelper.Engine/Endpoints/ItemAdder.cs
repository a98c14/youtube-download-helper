using System.Globalization;
using Microsoft.EntityFrameworkCore;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Presets;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Scheduling;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Endpoints;

/// <summary>
/// <c>POST /items</c>: validates the inputs, drops duplicates (in the request, in the queue, in the download archive)
/// unless forced, and queues the rest. Adds are serialized, so two requests cannot both add the same video.
/// </summary>
public sealed class ItemAdder(
    QueueActor queue,
    IDbContextFactory<StateDbContext> contextFactory,
    SettingsStore settingsStore,
    PresetRegistry presets) : IDisposable
{
    /// <summary>The most URLs and video IDs one request may add.</summary>
    public const int MaxInputs = 1000;

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

        var candidates = new List<Candidate>();
        foreach (var input in urls)
        {
            candidates.Add(FromUrl(input));
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

    private async Task<List<AddItemResult>> AddCandidatesAsync(List<Candidate> candidates, AddItemsRequest request, CancellationToken cancellationToken)
    {
        var presetId = request.PresetId;
        if (presetId is null)
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            presetId = presets.Contains(settings.DefaultPresetId) ? settings.DefaultPresetId : presets.Default.Id;
        }

        var snapshot = await queue.GetSnapshotAsync(cancellationToken);
        var queuedByUrl = new Dictionary<string, long>(StringComparer.Ordinal);
        var queuedByVideo = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items)
        {
            queuedByUrl.TryAdd(item.Url, item.Id);
            if (item.VideoId is { } id && string.Equals(item.Extractor, YouTubeUrls.Extractor, StringComparison.Ordinal))
            {
                queuedByVideo.TryAdd(id, item.Id);
            }
        }

        Dictionary<string, long> downloaded;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            downloaded = await db.GetDownloadedItemIdsAsync(
                YouTubeUrls.Extractor,
                candidates.Where(c => c.VideoId is not null).Select(c => c.VideoId!),
                cancellationToken);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<AddItemResult>();
        foreach (var candidate in candidates)
        {
            if (candidate.Invalid is { } invalid)
            {
                results.Add(new AddItemResult(candidate.Input, AddItemOutcome.Invalid, null, invalid));
                continue;
            }

            var key = candidate.VideoId is { } videoId ? "v:" + videoId : "u:" + candidate.Url;
            if (!seen.Add(key))
            {
                results.Add(Duplicate(candidate.Input, ItemCodes.DuplicateInRequest, null, "It is listed more than once."));
                continue;
            }

            if (!request.Force)
            {
                if ((candidate.VideoId is not null && queuedByVideo.TryGetValue(candidate.VideoId, out var queuedId))
                    || queuedByUrl.TryGetValue(candidate.Url!, out queuedId))
                {
                    results.Add(Duplicate(candidate.Input, ItemCodes.AlreadyQueued, queuedId, "It is already in the queue."));
                    continue;
                }

                if (candidate.VideoId is not null && downloaded.TryGetValue(candidate.VideoId, out var downloadedId))
                {
                    results.Add(Duplicate(candidate.Input, ItemCodes.AlreadyDownloaded, downloadedId, "It was already downloaded."));
                    continue;
                }
            }

            var metadata = candidate.VideoId is null ? null : new ItemMetadata(Extractor: YouTubeUrls.Extractor, VideoId: candidate.VideoId);
            var added = await queue.AddAsync(
                new AddItemRequest(candidate.Url!, presetId, request.Folder, request.Force, request.Paused, Metadata: metadata),
                CancellationToken.None);
            results.Add(new AddItemResult(candidate.Input, AddItemOutcome.Added, added.Id, null));
        }

        return results;
    }

    private static Candidate FromUrl(string? input)
    {
        var url = input?.Trim();
        if (url is null
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
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

    private static AddItemResult Duplicate(string input, string code, long? itemId, string message)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["input"] = input };
        if (itemId is { } id)
        {
            args["itemId"] = id.ToString(CultureInfo.InvariantCulture);
        }

        return new AddItemResult(input, AddItemOutcome.Duplicate, itemId, new EngineMessage(code, args, message));
    }

    private static EngineMessage InputMessage(string code, string? input, string message) =>
        new(code, new Dictionary<string, string>(StringComparer.Ordinal) { ["input"] = input ?? string.Empty }, message);

    /// <summary>One input: <see cref="Url"/> to add, or why it is <see cref="Invalid"/>.</summary>
    private sealed record Candidate(string Input, string? Url, string? VideoId, EngineMessage? Invalid);
}
