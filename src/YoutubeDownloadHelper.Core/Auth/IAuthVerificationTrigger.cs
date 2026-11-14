using System.Threading.Channels;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// Asks the auth layer to verify the cookies, because an item failed with an auth error and now waits in
/// <c>WaitingAuth</c> (PLAN.md §5.2). <see cref="AuthVerifier"/> runs the verification (at most once per
/// <see cref="AuthOptions.ErrorVerificationInterval"/>) and requeues the waiting items when valid cookies are back.
/// <see cref="NoAuthVerificationTrigger"/> verifies nothing.
/// </summary>
public interface IAuthVerificationTrigger
{
    /// <summary>Starts a verification in the background. Must return quickly and never throw.</summary>
    void RequestVerification();
}

/// <summary>Does nothing: nothing verifies the cookies.</summary>
public sealed class NoAuthVerificationTrigger : IAuthVerificationTrigger
{
    public void RequestVerification()
    {
    }
}

/// <summary>
/// What the <see cref="AuthVerifier"/> reads: the scheduler's <see cref="IAuthVerificationTrigger"/> requests, and the
/// <see cref="AuthManager"/>'s note that the cookies changed (a login, import or log-out, which the verifier sees as a new
/// lineage and verifies without the throttle). A separate object so the manager and the verifier do not depend on each
/// other.
/// </summary>
public sealed class AuthVerificationRequests : IAuthVerificationTrigger
{
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    private int _pending;

    public void RequestVerification()
    {
        Volatile.Write(ref _pending, 1);
        _wake.Writer.TryWrite(true);
    }

    /// <summary>A login, import or log-out changed the lineage: wakes the verifier, which is not a throttled request.</summary>
    public void CookiesChanged() => Wake();

    /// <summary>Whether a verification was asked for since the last call; clears the request.</summary>
    internal bool TakePending() => Interlocked.Exchange(ref _pending, 0) == 1;

    /// <summary>Wakes the verifier's loop (also for its own explicit requests).</summary>
    internal void Wake() => _wake.Writer.TryWrite(true);

    /// <summary>Completes when a request or <see cref="Wake"/> arrives (or at once if one is waiting).</summary>
    internal Task WaitAsync(CancellationToken cancellationToken) => _wake.Reader.ReadAsync(cancellationToken).AsTask();
}
