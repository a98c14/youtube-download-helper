using System.ComponentModel;
using Microsoft.Extensions.Logging;
using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Core.Errors;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Runner;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Auth;

public enum AuthProbeOutcome
{
    /// <summary>The cookies are logged in.</summary>
    Valid,

    /// <summary>YouTube answered that they are not (an Auth-category error).</summary>
    Invalid,

    /// <summary>No answer about the cookies: offline, tools missing, or another error. Says nothing about the login.</summary>
    Inconclusive,
}

/// <summary>What one verification run found.</summary>
/// <param name="Outcome">Whether the cookies are logged in.</param>
/// <param name="Account">The account name, when the run showed it.</param>
/// <param name="WrittenBack">The cookies yt-dlp saved back, to merge like a job's writeback; null when there are none.</param>
/// <param name="Error">Why it was not <see cref="AuthProbeOutcome.Valid"/>.</param>
public sealed record AuthProbeResult(AuthProbeOutcome Outcome, string? Account, CookieJar? WrittenBack, EngineMessage? Error)
{
    public static AuthProbeResult Inconclusive(EngineMessage error) => new(AuthProbeOutcome.Inconclusive, null, null, error);
}

/// <summary>
/// One verification of a cookie jar (PLAN.md §5.7 "Verification"). <see cref="YtDlpAuthProbe"/> is the engine's: a flat
/// fetch of the subscriptions feed with yt-dlp. Tests script it to reach each step of the recovery ladder.
/// </summary>
public interface IAuthProbe
{
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    Task<AuthProbeResult> ProbeAsync(CookieJar jar, CancellationToken cancellationToken);
}

/// <summary>
/// Verifies a jar with yt-dlp: writes it to <c>work\verify-&lt;guid&gt;\cookies.txt</c> (readable only by the user), runs
/// <see cref="YtDlpCommandBuilder.VerifyAuth"/>, reads the cookies yt-dlp saved back, and deletes the folder. Success is
/// <see cref="AuthProbeOutcome.Valid"/>; an Auth-category error <see cref="AuthProbeOutcome.Invalid"/>; anything else
/// (offline, tools missing or broken) <see cref="AuthProbeOutcome.Inconclusive"/>. yt-dlp's flat listing of the feed
/// does not name the account, so <see cref="AuthProbeResult.Account"/> stays null.
/// </summary>
public sealed partial class YtDlpAuthProbe(
    YtDlpCommandBuilder builder,
    YtDlpRunner runner,
    ErrorClassifier classifier,
    IToolHealth toolHealth,
    DataPaths paths,
    AuthOptions options,
    ILogger<YtDlpAuthProbe> logger) : IAuthProbe
{
    private const string DirPrefix = "verify-";

    public static EngineMessage ToolsMissingMessage { get; } = new(
        Scheduling.SchedulerCodes.ToolsMissing, "The download tools are missing or being repaired.");

    public async Task<AuthProbeResult> ProbeAsync(CookieJar jar, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jar);
        if (!toolHealth.ToolsReady)
        {
            return AuthProbeResult.Inconclusive(ToolsMissingMessage);
        }

        var folder = Path.Combine(paths.WorkDir, DirPrefix + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, CookieFileProvider.FileName);
        try
        {
            try
            {
                Directory.CreateDirectory(folder);
                UserOnlyFile.WriteAllText(file, NetscapeCookieFile.Write(jar));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogWriteFailed(logger, file, ex);
                return AuthProbeResult.Inconclusive(new EngineMessage(ErrorCodes.Unknown, "The cookies file could not be written."));
            }

            YtDlpJsonResult result;
            try
            {
                result = await runner.RunJsonAsync(builder.VerifyAuth(file, options.VerificationUrl), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                LogStartFailed(logger, ex.NativeErrorCode);
                return AuthProbeResult.Inconclusive(ToolsMissingMessage);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var written = ReadBack(file);
            if (result.Run.Succeeded)
            {
                return new AuthProbeResult(AuthProbeOutcome.Valid, null, written, null);
            }

            var error = classifier.Classify(result.Run.ExitCode, result.Run.OutputTail);
            return new AuthProbeResult(
                error.Category == ErrorCategory.Auth ? AuthProbeOutcome.Invalid : AuthProbeOutcome.Inconclusive,
                null,
                written,
                error.Error);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private CookieJar? ReadBack(string file)
    {
        try
        {
            return File.Exists(file) ? NetscapeCookieFile.Parse(File.ReadAllText(file)).Jar : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogReadFailed(logger, file, ex);
            return null;
        }
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The data folder's start-up cleanup deletes what is left.
            LogDeleteFailed(logger, folder, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the verification cookies file {Path}")]
    private static partial void LogWriteFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "yt-dlp could not be started for the auth verification (error {Error})")]
    private static partial void LogStartFailed(ILogger logger, int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the cookies the verification wrote back to {Path}")]
    private static partial void LogReadFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the verification folder {Path}")]
    private static partial void LogDeleteFailed(ILogger logger, string path, Exception exception);
}
