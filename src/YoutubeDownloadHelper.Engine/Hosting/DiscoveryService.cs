using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using YoutubeDownloadHelper.Contracts;
using YoutubeDownloadHelper.Contracts.Discovery;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// Publishes the engine once it listens: records the actual port in <see cref="EngineEndpoint"/> and writes the
/// discovery file. Deletes the file again when the host has stopped cleanly.
/// </summary>
public sealed partial class DiscoveryService(
    IServer server,
    EngineEndpoint endpoint,
    ApiToken token,
    DataPaths paths,
    ILogger<DiscoveryService> logger) : IHostedLifecycleService
{
    // Runs after every hosted service has started, the server included, so the port is known.
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var port = ListeningPort() ?? endpoint.RequestedPort;
        endpoint.SetListening(port);
        if (endpoint.RequestedPort != 0 && endpoint.RequestedPort != port)
        {
            LogPortTaken(endpoint.RequestedPort, port);
        }

        var baseUrl = endpoint.BaseUrl!;
        DiscoveryFile.Write(
            paths.EngineJson,
            new EngineDiscovery(ApiRoutes.Version, baseUrl, token.Value, Environment.ProcessId, EngineVersion.Current));
        LogListening(baseUrl, paths.EngineJson);
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        if (DiscoveryFile.DeleteIfOwned(paths.EngineJson, token.Value))
        {
            LogDeleted(paths.EngineJson);
        }

        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The port Kestrel bound, or <see langword="null"/> for a server without addresses (TestServer). Fails the start
    /// if the server listens anywhere but on one <c>http://127.0.0.1</c> address.
    /// </summary>
    private int? ListeningPort()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
        if (addresses.Count == 0)
        {
            return null;
        }

        if (addresses.Count != 1
            || !Uri.TryCreate(addresses[0], UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The engine must listen on 127.0.0.1 only, not on {string.Join(", ", addresses)}.");
        }

        return uri.Port;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine listening on {BaseUrl}; discovery file {Path}")]
    private partial void LogListening(string baseUrl, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Requested} is taken; listening on {Port} instead")]
    private partial void LogPortTaken(int requested, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted the discovery file {Path}")]
    private partial void LogDeleted(string path);
}
