using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Engine.Events;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// Runs the <see cref="AuthVerifier"/> with the host: the start-up verification, the keep-alive ping and the recovery
/// ladder (PLAN.md §5.7). <see cref="EngineHost.StartAsync"/> starts the queue actor before the host, so the actor is up
/// when this starts.
/// </summary>
public sealed class AuthVerifierService(AuthVerifier verifier) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => verifier.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => verifier.StopAsync(cancellationToken);
}

/// <summary>A client is connected when the event stream has one (<see cref="IClientPresence"/>).</summary>
public sealed class EventStreamUiClientPresence(IClientPresence clients) : IUiClientPresence
{
    public bool AnyClientConnected => clients.ConnectedClients > 0;
}
