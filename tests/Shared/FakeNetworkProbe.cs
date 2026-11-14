using YoutubeDownloadHelper.Core.Scheduling;

namespace YoutubeDownloadHelper.Tests.Shared;

/// <summary>
/// A connectivity check the test controls (no network in tests): <see cref="Online"/> is what it answers, and
/// <see cref="Probes"/> counts the calls. Linked into the test projects that use it.
/// </summary>
public sealed class FakeNetworkProbe : INetworkProbe
{
    private int _probes;
    private bool _online;

    public bool Online
    {
        get => Volatile.Read(ref _online);
        set => Volatile.Write(ref _online, value);
    }

    public int Probes => Volatile.Read(ref _probes);

    public Task<bool> IsOnlineAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _probes);
        return Task.FromResult(Online);
    }
}
