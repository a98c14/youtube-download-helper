using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Core.Events;

namespace YoutubeDownloadHelper.Core.Auth;

/// <summary>New cookies were stored, or they were deleted (<c>auth.changed</c>). Carries the status as it is now.</summary>
public sealed record AuthChangedEvent(AuthInfo Auth) : EngineEvent;
