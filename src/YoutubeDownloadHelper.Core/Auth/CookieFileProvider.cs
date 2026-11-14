using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Queue;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// The cookies file of one yt-dlp run (PLAN.md §5.7): the current generation as a plaintext Netscape file, readable only
/// by the user, passed with <c>--cookies</c>. A job's file is <c>work\&lt;itemId&gt;\cookies.txt</c> and its resolve and
/// download share it; a resolve outside a job gets <c>work\resolve-&lt;guid&gt;\cookies.txt</c>. Afterwards the
/// cookies yt-dlp wrote back are merged (<see cref="AuthManager.MergeWritebackAsync"/>; nothing after an Auth failure),
/// and the file is deleted. No file when the status is <c>NotLoggedIn</c> or <c>Expired</c>.
/// <para>
/// A file that cannot be written means a run without cookies; a writeback that cannot be read or merged is logged and
/// dropped. Neither fails the run.
/// </para>
/// </summary>
public sealed partial class CookieFileProvider(AuthManager auth, DataPaths paths, ILogger<CookieFileProvider> logger)
    : IJobCookieProvider, IResolveCookieProvider
{
    public const string FileName = "cookies.txt";

    private const string ResolveDirPrefix = "resolve-";

    private readonly ConcurrentDictionary<string, CookieCheckout> _given = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cookies files handed out and not yet merged back.</summary>
    public int Outstanding => _given.Count;

    public Task<string?> BeforeJobAsync(QueueItem item, string workDir, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        return WriteAsync(Path.Combine(workDir, FileName), cancellationToken);
    }

    public Task AfterJobAsync(QueueItem item, string? cookiesFile, JobOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return ReadBackAsync(cookiesFile, outcome.FailedWithAuth, deleteFolder: false, cancellationToken);
    }

    public Task<string?> BeforeResolveAsync(CancellationToken cancellationToken) =>
        WriteAsync(Path.Combine(paths.WorkDir, ResolveDirPrefix + Guid.NewGuid().ToString("N"), FileName), cancellationToken);

    public Task AfterResolveAsync(string? cookiesFile, bool failedWithAuth, CancellationToken cancellationToken) =>
        ReadBackAsync(cookiesFile, failedWithAuth, deleteFolder: true, cancellationToken);

    private async Task<string?> WriteAsync(string path, CancellationToken cancellationToken)
    {
        if (await auth.CheckOutAsync(cancellationToken).ConfigureAwait(false) is not { } checkout)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            UserOnlyFile.WriteAllText(path, NetscapeCookieFile.Write(checkout.Generation.Jar));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(logger, path, ex);
            TryDelete(path, deleteFolder: false);
            return null;
        }

        _given[path] = checkout;
        return path;
    }

    private async Task ReadBackAsync(string? path, bool failedWithAuth, bool deleteFolder, CancellationToken cancellationToken)
    {
        if (path is null || !_given.TryRemove(path, out var checkout))
        {
            return;
        }

        try
        {
            string text;
            try
            {
                text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // E.g. the item was removed and its work folder deleted with the file.
                return;
            }

            await auth.MergeWritebackAsync(checkout, NetscapeCookieFile.Parse(text).Jar, failedWithAuth, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogReadFailed(logger, path, ex);
        }
        finally
        {
            TryDelete(path, deleteFolder);
        }
    }

    private void TryDelete(string path, bool deleteFolder)
    {
        try
        {
            File.Delete(path);
            if (deleteFolder && Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The data folder's start-up cleanup deletes what is left (every work folder that is not an active item's).
            LogDeleteFailed(logger, path, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the cookies file {Path}; the run goes without cookies")]
    private static partial void LogWriteFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the cookies written back to {Path}; nothing is merged")]
    private static partial void LogReadFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the cookies file {Path}")]
    private static partial void LogDeleteFailed(ILogger logger, string path, Exception exception);
}
