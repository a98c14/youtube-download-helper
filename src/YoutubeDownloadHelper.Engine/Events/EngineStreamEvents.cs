using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Core.Events;

namespace YoutubeDownloadHelper.Engine.Events;

/// <summary>The engine is stopping (<c>engine.stopping</c>). Published when the host starts to stop.</summary>
public sealed record EngineStoppingEvent : EngineEvent;

/// <summary>
/// Something to tell the user (<c>notify</c>). The <see cref="EventJournal"/> publishes it after the event it is about,
/// so it has a sequence number of its own.
/// </summary>
public sealed record NotifyEvent(EngineMessage Message, long? ItemId) : EngineEvent;
