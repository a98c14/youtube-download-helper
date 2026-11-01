namespace YoutubeDownloadHelper.Core.Events;

/// <summary>
/// An event on the internal <see cref="EventBus"/>. The bus assigns <see cref="Sequence"/> on publish; the HTTP layer
/// (#45) turns these into SSE events whose <c>id</c> is the sequence number (PLAN.md §6.3).
/// </summary>
public abstract record EngineEvent
{
    /// <summary>Strictly increasing across every event the bus publishes; 0 until published.</summary>
    public long Sequence { get; init; }
}
