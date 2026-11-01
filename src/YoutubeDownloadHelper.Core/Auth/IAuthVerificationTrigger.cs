namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// Asks the auth layer to verify the cookies, because an item failed with an auth error and now waits in
/// <c>WaitingAuth</c> (PLAN.md §5.2). When valid cookies are back, the auth layer requeues the waiting items with
/// <c>QueueActor.ReleaseWaitingAsync(ItemTrigger.AuthRestored)</c>. <see cref="NoAuthVerificationTrigger"/> is the
/// default; the verification work (#49) replaces it.
/// </summary>
public interface IAuthVerificationTrigger
{
    /// <summary>Starts a verification in the background. Must return quickly and never throw.</summary>
    void RequestVerification();
}

/// <summary>Does nothing: nothing verifies the cookies yet.</summary>
public sealed class NoAuthVerificationTrigger : IAuthVerificationTrigger
{
    public void RequestVerification()
    {
    }
}
