namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// Whether a client (the UI) is connected to the event stream and so could run a browser refresh when the engine asks
/// for one (step (c) of the recovery ladder, PLAN.md §5.7). The engine host implements it with its event stream;
/// <see cref="NoUiClientPresence"/> is the default.
/// </summary>
public interface IUiClientPresence
{
    bool AnyClientConnected { get; }
}

/// <summary>No client is ever connected: the recovery ladder skips the browser-refresh step.</summary>
public sealed class NoUiClientPresence : IUiClientPresence
{
    public bool AnyClientConnected => false;
}
