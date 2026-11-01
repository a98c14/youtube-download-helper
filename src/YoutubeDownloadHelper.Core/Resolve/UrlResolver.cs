using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Resolve;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Core.Errors;
using YoutubeDownloadHelper.Core.Runner;
using YoutubeDownloadHelper.Core.Scheduling;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Resolve;

/// <summary>Why a <see cref="UrlResolution"/> has no result.</summary>
public enum ResolveFailure
{
    /// <summary>yt-dlp is missing or not working (<see cref="IToolHealth"/>, or it could not be started).</summary>
    ToolsMissing,

    /// <summary>yt-dlp failed; the message is its classified error.</summary>
    Failed,

    /// <summary>yt-dlp succeeded but printed neither a video nor a playlist.</summary>
    Unsupported,
}

/// <summary>A resolved URL: exactly one of <see cref="Video"/>, <see cref="Listing"/> or <see cref="Failure"/>.</summary>
/// <param name="Video">The single video.</param>
/// <param name="Listing">The playlist or channel (tab), flat, with at most the requested number of entries.</param>
/// <param name="Truncated">The listing had more entries than requested.</param>
/// <param name="Failure">Why there is no result.</param>
/// <param name="Error">The message for <paramref name="Failure"/>: a <see cref="ResolveCodes"/> or <see cref="ErrorCodes"/> code.</param>
/// <param name="Category">The classified error category when yt-dlp failed.</param>
public sealed record UrlResolution(
    ResolvedVideo? Video,
    ResolvedListing? Listing,
    bool Truncated,
    ResolveFailure? Failure,
    EngineMessage? Error,
    ErrorCategory? Category);

/// <summary>
/// Resolves a URL without downloading (PLAN.md §6.2 <c>POST /resolve</c>, playlist expansion): a single YouTube video URL
/// with <c>-J --no-playlist</c>, anything else with a flat listing (<c>--flat-playlist -J</c>), which prints a video's
/// metadata for a video and the entries for a playlist or channel (tab). At most <see cref="MaxConcurrentRuns"/> run at
/// once. Thread-safe.
/// </summary>
public sealed partial class UrlResolver(
    YtDlpCommandBuilder builder,
    YtDlpRunner runner,
    ErrorClassifier classifier,
    IToolHealth toolHealth,
    IResolveCookieProvider cookies,
    ILogger<UrlResolver> logger) : IDisposable
{
    /// <summary>How many yt-dlp resolves may run at once; more wait.</summary>
    public const int MaxConcurrentRuns = 4;

    private readonly SemaphoreSlim _runs = new(MaxConcurrentRuns, MaxConcurrentRuns);

    /// <param name="url">An absolute http(s) URL.</param>
    /// <param name="maxEntries">The most playlist entries to return.</param>
    /// <param name="cancellationToken">Cancelling kills yt-dlp.</param>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task<UrlResolution> ResolveAsync(string url, int maxEntries, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);

        if (!toolHealth.ToolsReady)
        {
            return ToolsMissing(url);
        }

        await _runs.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync(url, maxEntries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runs.Release();
        }
    }

    public void Dispose() => _runs.Dispose();

    private async Task<UrlResolution> RunAsync(string url, int maxEntries, CancellationToken cancellationToken)
    {
        var cookiesFile = await cookies.BeforeResolveAsync(cancellationToken).ConfigureAwait(false);
        var failedWithAuth = false;
        try
        {
            // One more entry than asked shows whether the listing was cut short.
            var command = YouTubeUrls.TryGetVideoId(url) is not null
                ? builder.Resolve(url, cookiesFile)
                : builder.FlatListing(url, maxEntries == int.MaxValue ? maxEntries : maxEntries + 1, cookiesFile);

            YtDlpJsonResult result;
            try
            {
                result = await runner.RunJsonAsync(command, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception e)
            {
                LogStartFailed(logger, e.NativeErrorCode);
                return ToolsMissing(url);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Run.Succeeded)
            {
                var classification = classifier.Classify(result.Run.ExitCode, result.Run.OutputTail);
                failedWithAuth = classification.Category == ErrorCategory.Auth;
                return new UrlResolution(null, null, false, ResolveFailure.Failed, classification.Error, classification.Category);
            }

            return Parse(url, result.Json, maxEntries);
        }
        finally
        {
            await cookies.AfterResolveAsync(cookiesFile, failedWithAuth, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static UrlResolution Parse(string url, string json, int maxEntries)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (ResolvedListing.Parse(document.RootElement) is { } listing)
            {
                var truncated = listing.Entries.Count > maxEntries;
                if (truncated)
                {
                    listing = listing with { Entries = [.. listing.Entries.Take(maxEntries)] };
                }

                return new UrlResolution(null, listing, truncated, null, null, null);
            }

            if (ResolvedVideo.Parse(document.RootElement) is { } video)
            {
                return new UrlResolution(video, null, false, null, null, null);
            }
        }
        catch (JsonException)
        {
        }

        return new UrlResolution(null, null, false, ResolveFailure.Unsupported, Message(ResolveCodes.Unsupported, url, "yt-dlp found no video or playlist at this URL."), null);
    }

    private static UrlResolution ToolsMissing(string url) =>
        new(null, null, false, ResolveFailure.ToolsMissing, Message(ResolveCodes.ToolsMissing, url, "yt-dlp is not installed or not working yet."), null);

    private static EngineMessage Message(string code, string url, string message) =>
        new(code, new Dictionary<string, string>(StringComparer.Ordinal) { ["input"] = url }, message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "yt-dlp could not be started for a resolve (error {Error})")]
    private static partial void LogStartFailed(ILogger logger, int error);
}
