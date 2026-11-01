using YoutubeDownloadHelper.Contracts.Messages;

namespace YoutubeDownloadHelper.Contracts.Queue;

/// <summary>The body of <c>POST</c> <see cref="ApiRoutes.Items"/>. Give <see cref="Urls"/>, <see cref="VideoIds"/>, or both.</summary>
/// <param name="Urls">Video URLs (http or https). Each URL is one item; playlists are not expanded.</param>
/// <param name="VideoIds">YouTube video IDs (11 characters), added as <c>https://www.youtube.com/watch?v=&lt;id&gt;</c>.</param>
/// <param name="PresetId">The preset (<c>PresetIds</c>); by default the <c>defaultPresetId</c> setting.</param>
/// <param name="Folder">A destination folder for these items; by default the download folder setting.</param>
/// <param name="Paused">Add the items <see cref="ItemState.Paused"/>.</param>
/// <param name="Force">Add (and download) videos that are already queued or downloaded.</param>
public sealed record AddItemsRequest(
    IReadOnlyList<string>? Urls = null,
    IReadOnlyList<string>? VideoIds = null,
    string? PresetId = null,
    string? Folder = null,
    bool Paused = false,
    bool Force = false);

/// <summary>What happened to one URL or video ID of an <see cref="AddItemsRequest"/>.</summary>
public enum AddItemOutcome
{
    /// <summary>A new item was queued.</summary>
    Added,

    /// <summary>
    /// Not added: the video is already in the queue or in the download archive (or listed twice in the request).
    /// <see cref="AddItemResult.ItemId"/> is the existing item, when known.
    /// </summary>
    Duplicate,

    /// <summary>Not added: not an http(s) URL or not a video ID. <see cref="AddItemResult.Message"/> says why.</summary>
    Invalid,
}

/// <summary>The result for one input, in request order (URLs first, then video IDs).</summary>
/// <param name="Input">The URL or video ID as given.</param>
/// <param name="Outcome">Added, duplicate or invalid.</param>
/// <param name="ItemId">The new item (<see cref="AddItemOutcome.Added"/>) or the existing one (<see cref="AddItemOutcome.Duplicate"/>).</param>
/// <param name="Message">Why it was not added (<see cref="ItemCodes"/>); null when added.</param>
public sealed record AddItemResult(string Input, AddItemOutcome Outcome, long? ItemId, EngineMessage? Message);

/// <summary>The answer to <c>POST</c> <see cref="ApiRoutes.Items"/>.</summary>
/// <param name="Results">One result per input.</param>
public sealed record AddItemsResponse(IReadOnlyList<AddItemResult> Results);

/// <summary>
/// Message codes about items: <see cref="AddItemResult.Message"/> and the error bodies of the item commands. Every one has
/// the arg <c>input</c> or <c>itemId</c> as listed.
/// </summary>
public static class ItemCodes
{
    /// <summary>Not an absolute http(s) URL. Args: <c>input</c>.</summary>
    public const string InvalidUrl = "item.invalidUrl";

    /// <summary>Not an 11-character YouTube video ID. Args: <c>input</c>.</summary>
    public const string InvalidVideoId = "item.invalidVideoId";

    /// <summary>Already queued. Args: <c>input</c>, <c>itemId</c>.</summary>
    public const string AlreadyQueued = "item.alreadyQueued";

    /// <summary>Already downloaded (in the archive). Args: <c>input</c>, <c>itemId</c>.</summary>
    public const string AlreadyDownloaded = "item.alreadyDownloaded";

    /// <summary>Listed more than once in the same request. Args: <c>input</c>.</summary>
    public const string DuplicateInRequest = "item.duplicateInRequest";
}
