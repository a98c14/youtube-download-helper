namespace YoutubeDownloadHelper.Contracts.Queue;

/// <summary>
/// Message codes of rejected queue commands: the error bodies of the item and queue endpoints (<c>404</c> for
/// <see cref="ItemNotFound"/>, <c>409</c> for the others).
/// </summary>
public static class QueueCodes
{
    /// <summary>No such item (or it was removed). Args: <c>itemId</c>.</summary>
    public const string ItemNotFound = "queue.itemNotFound";

    /// <summary>The item's state does not allow the command. Args: <c>itemId</c>, <c>state</c>, <c>trigger</c>.</summary>
    public const string InvalidTransition = "queue.invalidTransition";

    /// <summary>The move is not possible, e.g. before itself or with no <c>beforeId</c>.</summary>
    public const string InvalidMove = "queue.invalidMove";

    /// <summary>The item is not running. Args: <c>itemId</c>, <c>state</c>.</summary>
    public const string NotRunning = "queue.notRunning";

    /// <summary>The whole queue is paused.</summary>
    public const string QueuePaused = "queue.paused";
}
