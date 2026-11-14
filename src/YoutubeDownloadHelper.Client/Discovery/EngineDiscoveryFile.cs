using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Discovery;
using YoutubeDownloadHelper.Contracts.Info;

namespace YoutubeDownloadHelper.Client.Discovery;

/// <summary>What <see cref="EngineDiscoveryFile.CheckAsync"/> found.</summary>
public enum DiscoveryStatus
{
    /// <summary>The engine runs and answered <c>GET /info</c>.</summary>
    Ready,

    /// <summary>There is no discovery file: no engine runs (or it has not finished starting).</summary>
    Missing,

    /// <summary>The file exists but cannot be read or parsed (e.g. it is being replaced, or it is damaged).</summary>
    Unreadable,

    /// <summary>The engine speaks another API version than this client.</summary>
    Incompatible,

    /// <summary>Stale: the engine's process is gone (it crashed or was killed, so it could not delete the file).</summary>
    ProcessExited,

    /// <summary>Stale: the process runs, but <c>GET /info</c> failed (it hangs, the token is wrong, or the PID was reused).</summary>
    Unresponsive,
}

/// <summary>The result of <see cref="EngineDiscoveryFile.CheckAsync"/>.</summary>
/// <param name="Status">What was found.</param>
/// <param name="Discovery">The file's content, when it could be read.</param>
/// <param name="Info">The engine's <c>GET /info</c> answer, when <see cref="DiscoveryStatus.Ready"/>.</param>
/// <param name="Error">Why <c>GET /info</c> failed, when <see cref="DiscoveryStatus.Unresponsive"/>.</param>
public sealed record DiscoveryCheck(DiscoveryStatus Status, EngineDiscovery? Discovery, EngineInfo? Info = null, Exception? Error = null)
{
    public bool IsReady => Status == DiscoveryStatus.Ready;
}

/// <summary>
/// Reads the engine's discovery file <c>engine.json</c> (PLAN.md §6.1). The file is treated as stale when the PID it
/// names is gone or the engine does not answer <c>GET /info</c>.
/// </summary>
public static class EngineDiscoveryFile
{
    /// <summary><c>%LOCALAPPDATA%\YoutubeDownloadHelperData\engine.json</c>: the installed engine's discovery file.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        AppIdentity.DataFolderName,
        EngineDiscovery.FileName);

    /// <summary>The discovery file at <paramref name="path"/>, or null when there is none or it cannot be read.</summary>
    public static EngineDiscovery? TryRead(string path) => Read(path).Discovery;

    /// <summary>Whether a process with this ID runs.</summary>
    public static bool IsProcessRunning(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // It exists, but this user may not inspect it (another user's or an elevated process).
            return true;
        }
    }

    /// <summary>
    /// Reads the discovery file and checks the engine it names: the process must run and answer <c>GET /info</c>
    /// within <paramref name="healthTimeout"/> (5 s by default).
    /// </summary>
    public static async Task<DiscoveryCheck> CheckAsync(
        string path,
        EngineClientOptions? options = null,
        TimeSpan? healthTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var read = Read(path);
        if (read.Discovery is not { } discovery)
        {
            return read;
        }

        if (discovery.ApiVersion != ApiRoutes.Version)
        {
            return new DiscoveryCheck(DiscoveryStatus.Incompatible, discovery);
        }

        if (!IsProcessRunning(discovery.Pid))
        {
            return new DiscoveryCheck(DiscoveryStatus.ProcessExited, discovery);
        }

        var clientOptions = options ?? new EngineClientOptions();
        using var client = new EngineClient(discovery, new EngineClientOptions
        {
            Handler = clientOptions.Handler,
            RequestTimeout = healthTimeout ?? TimeSpan.FromSeconds(5),
            EventStreamIdleTimeout = clientOptions.EventStreamIdleTimeout,
            TimeProvider = clientOptions.TimeProvider,
        });
        try
        {
            var info = await client.GetInfoAsync(cancellationToken);
            return info.ApiVersion == ApiRoutes.Version
                ? new DiscoveryCheck(DiscoveryStatus.Ready, discovery, info)
                : new DiscoveryCheck(DiscoveryStatus.Incompatible, discovery, info);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is HttpRequestException or EngineApiException or OperationCanceledException or JsonException)
        {
            return new DiscoveryCheck(DiscoveryStatus.Unresponsive, discovery, Error: ex);
        }
    }

    private static DiscoveryCheck Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = File.OpenRead(path);
            var discovery = JsonSerializer.Deserialize<EngineDiscovery>(stream, EngineJson.Options);
            return discovery is { BaseUrl: not null, Token: not null }
                && Uri.TryCreate(discovery.BaseUrl, UriKind.Absolute, out _)
                    ? new DiscoveryCheck(DiscoveryStatus.Ready, discovery)
                    : new DiscoveryCheck(DiscoveryStatus.Unreadable, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new DiscoveryCheck(DiscoveryStatus.Missing, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DiscoveryCheck(DiscoveryStatus.Unreadable, null);
        }
    }
}
