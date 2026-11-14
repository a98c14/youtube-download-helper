using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Core.Events;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>
/// New cookies were stored, they were deleted, or the verified status changed (<c>auth.changed</c>). Carries the status
/// as it is now, and the one before when the status changed.
/// </summary>
/// <param name="Auth">The login status now.</param>
/// <param name="PreviousStatus">The status before, when a verification changed it; null for a login, import or log-out.</param>
public sealed record AuthChangedEvent(AuthInfo Auth, AuthStatus? PreviousStatus = null) : EngineEvent;

/// <summary>
/// Step (c) of the recovery ladder (PLAN.md §5.7): the cookies failed verification, and the engine asks a connected
/// client for a browser refresh (<c>auth.refreshRequested</c>).
/// </summary>
public sealed record AuthRefreshRequestedEvent : EngineEvent;

/// <summary>
/// A browser-refresh lease began or ended (<see cref="BrowserRefreshLease"/>). Not shown by the API; it wakes the
/// scheduler, whose cookie-using job starts wait while a lease is held.
/// </summary>
/// <param name="Held">Whether a lease holds job starts now.</param>
public sealed record AuthRefreshLeaseChangedEvent(bool Held) : EngineEvent;
