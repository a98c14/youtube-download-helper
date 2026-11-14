namespace YoutubeDownloadHelper.Contracts.Auth;

/// <summary>The answer to <c>POST</c> <see cref="ApiRoutes.AuthVerify"/>.</summary>
/// <param name="Auth">The login status after the verification.</param>
/// <param name="Recovering">
/// The cookies failed verification, and the engine has asked a connected client for a browser refresh
/// (<see cref="Events.EventTypes.AuthRefreshRequested"/>) before it gives up on them. <see cref="Events.EventTypes.AuthChanged"/>
/// follows with the outcome when the status changes.
/// </param>
public sealed record AuthVerifyResponse(AuthInfo Auth, bool Recovering);

/// <summary>The answer to <c>POST</c> <see cref="ApiRoutes.AuthRefreshBegin"/>: the lease and the jar to load into the browser.</summary>
/// <param name="LeaseId">Pass it back to <see cref="ApiRoutes.AuthRefreshComplete"/>.</param>
/// <param name="ExpiresAt">When the lease ends on its own; a later <c>complete</c> is refused.</param>
/// <param name="Cookies">The engine's current cookies. A leading dot on the domain makes a domain cookie.</param>
public sealed record AuthRefreshBeginResponse(string LeaseId, DateTimeOffset ExpiresAt, IReadOnlyList<CookieInfo> Cookies);

/// <summary>The body of <c>PUT</c> <see cref="ApiRoutes.AuthRefreshComplete"/>.</summary>
/// <param name="LeaseId">What <see cref="ApiRoutes.AuthRefreshBegin"/> returned.</param>
/// <param name="Cookies">The browser's cookies after the refresh. Only <c>youtube.com</c> cookies are kept.</param>
public sealed record AuthRefreshCompleteRequest(string LeaseId, IReadOnlyList<CookieInfo> Cookies);

/// <summary>The answer to <c>PUT</c> <see cref="ApiRoutes.AuthRefreshComplete"/> when the new cookies were verified and kept.</summary>
/// <param name="Auth">The login status with the new cookies.</param>
/// <param name="Stored">How many cookies were stored (the <c>youtube.com</c> ones).</param>
public sealed record AuthRefreshCompleteResponse(AuthInfo Auth, int Stored);

/// <summary>
/// The data of the <see cref="Events.EventTypes.AuthRefreshRequested"/> event: the stored cookies failed verification,
/// and the engine waits for a browser refresh (<see cref="ApiRoutes.AuthRefreshBegin"/>) before it reports the login as
/// expired.
/// </summary>
public sealed record AuthRefreshRequestedData;
