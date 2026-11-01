using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Resolve;
using YoutubeDownloadHelper.Core.Resolve;
using YoutubeDownloadHelper.Core.Runner;
using YoutubeDownloadHelper.Core.Scheduling;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Endpoints;

public static class ResolveEndpoints
{
    public static IEndpointRouteBuilder MapResolveEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ApiRoutes.Resolve, ResolveAsync)
            .WithTags("Resolve")
            .WithName("resolve")
            .WithSummary("A video's metadata, or a playlist's or channel's entries (flat), each flagged when adding it would be a duplicate.")
            .WithDescription(
                "Runs yt-dlp; downloads nothing. A YouTube video URL resolves as a single video even when it names a playlist " +
                "(list=). A channel URL without a tab lists its tabs as entries of kind playlist. 422 with the yt-dlp error " +
                "(error.* code) when yt-dlp fails; 503 resolve.toolsMissing while yt-dlp is not installed.")
            .Produces<EngineMessage>(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    /// <summary>An absolute http(s) URL, trimmed, or null.</summary>
    internal static string? HttpUrl(string? input)
    {
        var url = input?.Trim();
        return url is not null
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;
    }

    /// <summary>The YouTube video ID of a resolved video or entry; null for other sites and for playlists.</summary>
    internal static string? YouTubeVideoId(string? extractor, string? id) =>
        string.Equals(extractor, YouTubeUrls.Extractor, StringComparison.Ordinal) && YouTubeUrls.IsVideoId(id) ? id : null;

    private static async Task<Results<Ok<ResolveResponse>, BadRequest<EngineMessage>, UnprocessableEntity<EngineMessage>, JsonHttpResult<EngineMessage>>> ResolveAsync(
        ResolveRequest request,
        UrlResolver resolver,
        DuplicateFinder duplicates,
        CancellationToken cancellationToken)
    {
        if (HttpUrl(request.Url) is not { } url)
        {
            return TypedResults.BadRequest(ApiErrors.InvalidParameter("url", "Not an http or https URL."));
        }

        var limit = request.Limit ?? ResolveLimits.DefaultEntries;
        if (limit is < 1 or > ResolveLimits.MaxEntries)
        {
            return TypedResults.BadRequest(ApiErrors.InvalidParameter(
                "limit", $"The limit must be between 1 and {ResolveLimits.MaxEntries.ToString(CultureInfo.InvariantCulture)}."));
        }

        var resolution = await resolver.ResolveAsync(url, limit, cancellationToken);
        if (resolution.Failure is { } failure)
        {
            return failure == ResolveFailure.ToolsMissing
                ? TypedResults.Json(resolution.Error!, statusCode: StatusCodes.Status503ServiceUnavailable)
                : TypedResults.UnprocessableEntity(resolution.Error!);
        }

        if (resolution.Video is { } video)
        {
            var entry = ToEntry(url, video);
            var index = await duplicates.LoadAsync(entry.VideoId is { } id ? [id] : [], cancellationToken);
            return TypedResults.Ok(new ResolveResponse(
                ResolveKind.Video, url, video.VideoId, video.Title, video.Channel, video.ChannelId, 1, false, [Flag(entry, index)]));
        }

        var listing = resolution.Listing!;
        var entries = listing.Entries.Select(ToEntry).ToList();
        var listed = await duplicates.LoadAsync(entries.Where(e => e.VideoId is not null).Select(e => e.VideoId!), cancellationToken);
        return TypedResults.Ok(new ResolveResponse(
            ResolveKind.Playlist,
            url,
            listing.Id,
            listing.Title,
            listing.Channel,
            listing.ChannelId,
            listing.PlaylistCount,
            resolution.Truncated,
            [.. entries.Select(e => Flag(e, listed))]));
    }

    private static ResolvedEntry Flag(ResolvedEntry entry, DuplicateIndex index) =>
        entry.Kind == ResolvedEntryKind.Video && index.Find(entry.Url, entry.VideoId) is { } found
            ? entry with { Duplicate = found.Kind, ItemId = found.ItemId }
            : entry;

    private static ResolvedEntry ToEntry(string url, ResolvedVideo video) => new(
        ResolvedEntryKind.Video,
        url,
        video.Extractor,
        YouTubeVideoId(video.Extractor, video.VideoId),
        video.Title,
        video.Channel,
        video.UploadDate,
        video.Duration?.TotalSeconds,
        video.Availability,
        video.LiveStatus,
        video.SizeEstimate,
        null,
        null);

    private static ResolvedEntry ToEntry(ListingEntry entry) => new(
        entry.IsPlaylist ? ResolvedEntryKind.Playlist : ResolvedEntryKind.Video,
        entry.Url,
        entry.Extractor,
        entry.IsPlaylist ? null : YouTubeVideoId(entry.Extractor, entry.Id),
        entry.Title,
        entry.Channel,
        entry.UploadDate,
        entry.Duration?.TotalSeconds,
        entry.Availability,
        entry.LiveStatus,
        null,
        null,
        null);
}
