namespace YoutubeDownloadHelper.Contracts.Queue;

/// <summary>Where <c>POST</c> <see cref="ApiRoutes.ItemMove"/> moves an item.</summary>
public enum MoveTarget
{
    Top,
    Bottom,

    /// <summary>Just before another item.</summary>
    Before,
}

/// <summary>The body of <c>POST</c> <see cref="ApiRoutes.ItemMove"/>.</summary>
/// <param name="To">Where to move the item.</param>
/// <param name="BeforeId">With <see cref="MoveTarget.Before"/>: the item to move it in front of.</param>
public sealed record MoveItemRequest(MoveTarget To, long? BeforeId = null);
