namespace YoutubeDownloadHelper.Client;

/// <summary>How an <see cref="EngineClient"/> talks to the engine.</summary>
public sealed class EngineClientOptions
{
    /// <summary>The HTTP handler to send requests with (not disposed by the client); a new one per client by default.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>How long a request may take. Does not apply to the event stream, which stays open.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// How long the event stream may stay silent before the client gives up on it. The engine sends a heartbeat
    /// every 15 s, so a longer silence means the connection is dead.
    /// </summary>
    public TimeSpan EventStreamIdleTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
