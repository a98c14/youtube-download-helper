using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Tools;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Installs one release of a tool into its version folder (PLAN.md §5.6): download into <c>tools\.staging\</c> →
/// verify the published SHA-256 → extract the binaries → smoke-test them → rename the staged folder to
/// <c>tools\&lt;tool&gt;\&lt;version&gt;\</c>. The rename is the only step that touches a version folder, so a version
/// folder is always complete, and an install that fails or is interrupted at any point leaves none behind. It does not
/// switch the current version; <see cref="ToolManager"/> does that, in the database.
/// </summary>
public sealed partial class ToolInstaller(
    DataPaths paths,
    IToolReleaseFeed feed,
    IToolSmokeTest smokeTest,
    TimeProvider time,
    ILogger<ToolInstaller> logger)
{
    /// <summary>How often, at most, download progress is reported.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Where installs are staged. Anything left in it is from an interrupted install.</summary>
    public string StagingDir => Path.Combine(paths.ToolsDir, ".staging");

    public string VersionDir(ManagedTool tool, string version)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return Path.Combine(paths.ToolsDir, tool.Name, version);
    }

    /// <summary>
    /// A version folder name must be a plain name, since it comes from the feed (and the database): letters, digits,
    /// <c>.</c>, <c>-</c> and <c>_</c>, starting with a letter or digit.
    /// </summary>
    public static bool IsValidVersion(string? version)
    {
        if (string.IsNullOrEmpty(version) || version.Length > 64 || !char.IsAsciiLetterOrDigit(version[0]))
        {
            return false;
        }

        foreach (var c in version)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Installs the feed's latest release of <paramref name="tool"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired; nothing was installed.</exception>
    public async Task<ToolInstallResult> InstallLatestAsync(
        ManagedTool tool, Action<ToolInstallProgressEvent>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ToolRelease release;
        try
        {
            release = await feed.GetLatestAsync(tool, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolFeedException ex)
        {
            LogFailed(logger, tool.Name, ex);
            return ToolInstallResult.Failure(Message(ToolCodes.AssetMissing, tool, $"The latest {tool.Name} release has no file for Windows."));
        }
        catch (Exception ex) when (IsDownloadError(ex, cancellationToken))
        {
            LogFailed(logger, tool.Name, ex);
            return ToolInstallResult.Failure(DownloadFailed(tool));
        }

        return await InstallAsync(tool, release, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs <paramref name="release"/> of <paramref name="tool"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired; nothing was installed.</exception>
    public async Task<ToolInstallResult> InstallAsync(
        ManagedTool tool, ToolRelease release, Action<ToolInstallProgressEvent>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(release);
        var version = release.Version;
        if (!IsValidVersion(version) || !string.Equals(Path.GetFileName(release.AssetName), release.AssetName, StringComparison.Ordinal))
        {
            return ToolInstallResult.Failure(Message(ToolCodes.Failed, tool, $"The {tool.Name} release has an unusable version or file name."));
        }

        void Report(ToolInstallPhase phase, long received = 0) =>
            progress?.Invoke(new ToolInstallProgressEvent(tool.Name, version, phase, received, release.Size));

        var staging = Path.Combine(StagingDir, $"{tool.Name}-{Guid.NewGuid():N}");
        try
        {
            LogInstalling(logger, tool.Name, version);
            var downloadDir = Directory.CreateDirectory(Path.Combine(staging, "download")).FullName;
            var download = Path.Combine(downloadDir, release.AssetName);

            Report(ToolInstallPhase.Downloading);
            var lastReport = time.GetTimestamp();
            await using (var file = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await feed.DownloadAsync(release.AssetUri, file, received =>
                {
                    if (time.GetElapsedTime(lastReport) >= ProgressInterval || received == release.Size)
                    {
                        lastReport = time.GetTimestamp();
                        Report(ToolInstallPhase.Downloading, received);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }

            Report(ToolInstallPhase.Verifying);
            await VerifyAsync(tool, release, download, cancellationToken).ConfigureAwait(false);

            Report(ToolInstallPhase.Extracting);
            var content = Path.Combine(staging, "content");
            Extract(tool, download, content);

            Report(ToolInstallPhase.Testing);
            if (await SmokeTestAsync(tool, content, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                LogSmokeTestFailed(logger, tool.Name, version, failure.File, failure.Result.Output);
                throw new ToolInstallException(new EngineMessage(
                    ToolCodes.SmokeTestFailed,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["tool"] = tool.Name, ["file"] = failure.File },
                    $"{failure.File} from the new {tool.Name} release does not run."));
            }

            Report(ToolInstallPhase.Switching);
            var target = VersionDir(tool, version);
            await MoveIntoPlaceAsync(tool, content, target, Path.Combine(staging, "replaced"), cancellationToken).ConfigureAwait(false);
            LogInstalled(logger, tool.Name, version);
            return ToolInstallResult.Success(version, target, release.Channel);
        }
        catch (ToolInstallException ex)
        {
            LogRejected(logger, tool.Name, version, ex.Reason.Code);
            return ToolInstallResult.Failure(ex.Reason);
        }
        catch (Exception ex) when (IsDownloadError(ex, cancellationToken))
        {
            LogFailed(logger, tool.Name, ex);
            return ToolInstallResult.Failure(DownloadFailed(tool));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogFailed(logger, tool.Name, ex);
            return ToolInstallResult.Failure(Message(ToolCodes.Failed, tool, $"{tool.Name} could not be installed: {ex.Message}"));
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Whether <paramref name="directory"/> holds every binary of <paramref name="tool"/> and they pass the smoke test
    /// (when <paramref name="smoke"/>).
    /// </summary>
    public async Task<bool> IsHealthyAsync(ManagedTool tool, string directory, bool smoke, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!HasFiles(tool, directory))
        {
            return false;
        }

        if (smoke && await SmokeTestAsync(tool, directory, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            LogSmokeTestFailed(logger, tool.Name, Path.GetFileName(directory), failure.File, failure.Result.Output);
            return false;
        }

        return true;
    }

    public static bool HasFiles(ManagedTool tool, string directory)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Executables.All(exe => File.Exists(Path.Combine(directory, exe)));
    }

    /// <summary>Deletes what interrupted installs left in <see cref="StagingDir"/>.</summary>
    public void CleanStaging()
    {
        if (Directory.Exists(StagingDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(StagingDir))
            {
                TryDelete(dir);
            }
        }
    }

    private async Task VerifyAsync(ManagedTool tool, ToolRelease release, string download, CancellationToken cancellationToken)
    {
        var checksums = await feed.GetTextAsync(release.ChecksumUri, cancellationToken).ConfigureAwait(false);
        var expected = ChecksumFile.FindSha256(checksums, release.AssetName)
            ?? throw new ToolInstallException(Message(ToolCodes.ChecksumMissing, tool, $"The {tool.Name} release publishes no checksum for {release.AssetName}."));

        string actual;
        await using (var file = new FileStream(download, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        }

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new ToolInstallException(Message(ToolCodes.ChecksumMismatch, tool, $"The {tool.Name} download does not match its published checksum."));
        }
    }

    /// <summary>A zip gives up just the binaries (wherever they are in it); a bare executable is the binary.</summary>
    private static void Extract(ManagedTool tool, string download, string content)
    {
        Directory.CreateDirectory(content);
        var name = Path.GetFileName(download);
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(download);
            foreach (var exe in tool.Executables)
            {
                var entry = zip.Entries
                    .Where(e => string.Equals(e.Name, exe, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName.Length)
                    .FirstOrDefault()
                    ?? throw new ToolInstallException(Message(ToolCodes.AssetMissing, tool, $"The {tool.Name} download has no {exe}."));
                entry.ExtractToFile(Path.Combine(content, exe));
            }
        }
        else if (tool.Executables.Count == 1 && string.Equals(name, tool.MainExecutable, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(download, Path.Combine(content, tool.MainExecutable));
        }
        else
        {
            throw new ToolInstallException(Message(ToolCodes.AssetMissing, tool, $"The {tool.Name} download is not a zip or {tool.MainExecutable}."));
        }
    }

    private async Task<SmokeFailure?> SmokeTestAsync(ManagedTool tool, string directory, CancellationToken cancellationToken)
    {
        foreach (var exe in tool.Executables)
        {
            var result = await smokeTest.RunAsync(Path.Combine(directory, exe), tool.SmokeArguments, cancellationToken).ConfigureAwait(false);
            if (!result.Passed)
            {
                return new SmokeFailure(exe, result);
            }
        }

        return null;
    }

    /// <summary>
    /// Renames the staged folder to the version folder (same volume, so atomic). A version folder that is already there
    /// is kept when it is healthy (an earlier install got this far but did not switch); a broken one (e.g. antivirus
    /// took a binary) is moved out of the way first, into the staging folder that is deleted afterwards.
    /// </summary>
    private async Task MoveIntoPlaceAsync(ManagedTool tool, string content, string target, string replaced, CancellationToken cancellationToken)
    {
        if (Directory.Exists(target))
        {
            if (await IsHealthyAsync(tool, target, smoke: true, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            Directory.Move(target, replaced);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(content, target);
    }

    private static bool IsDownloadError(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or HttpIOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        || (ex is IOException && ex.InnerException is OperationCanceledException or HttpRequestException);

    private static EngineMessage DownloadFailed(ManagedTool tool) =>
        Message(ToolCodes.DownloadFailed, tool, $"{tool.Name} could not be downloaded. Check the connection.");

    private static EngineMessage Message(string code, ManagedTool tool, string message) =>
        new(code, new Dictionary<string, string>(StringComparer.Ordinal) { ["tool"] = tool.Name }, message);

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(logger, directory, ex);
        }
    }

    private sealed record SmokeFailure(string File, ToolSmokeResult Result);

    private sealed class ToolInstallException(EngineMessage reason) : Exception(reason.Message)
    {
        public EngineMessage Reason { get; } = reason;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing {Tool} {Version}")]
    private static partial void LogInstalling(ILogger logger, string tool, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installed {Tool} {Version}")]
    private static partial void LogInstalled(ILogger logger, string tool, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected {Tool} {Version}: {Code}")]
    private static partial void LogRejected(ILogger logger, string tool, string version, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Tool} {Version}: {File} failed its smoke test: {Output}")]
    private static partial void LogSmokeTestFailed(ILogger logger, string tool, string version, string file, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Installing {Tool} failed")]
    private static partial void LogFailed(ILogger logger, string tool, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete {Directory}")]
    private static partial void LogCleanupFailed(ILogger logger, string directory, Exception exception);
}

/// <summary>
/// The outcome of <see cref="ToolInstaller.InstallAsync"/>. On success: the installed <c>Version</c>, its version folder
/// (<c>Directory</c>) and release <c>Channel</c>; on failure, why (<c>Error</c>).
/// </summary>
public sealed record ToolInstallResult(bool Succeeded, string? Version, string? Directory, string? Channel, EngineMessage? Error)
{
    public static ToolInstallResult Success(string version, string directory, string? channel) => new(true, version, directory, channel, null);

    public static ToolInstallResult Failure(EngineMessage error) => new(false, null, null, null, error);
}
