using System.Globalization;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Core.Queue;

/// <summary>The outcome of a queue command: done, or rejected with a reason the caller can show.</summary>
public sealed record QueueResult(EngineMessage? Rejection)
{
    public static QueueResult Ok { get; } = new((EngineMessage?)null);

    public bool Succeeded => Rejection is null;

    internal static QueueResult NotFound(long itemId) =>
        new(new EngineMessage(
            QueueCodes.ItemNotFound,
            Args(("itemId", itemId.ToString(CultureInfo.InvariantCulture))),
            $"Item {itemId.ToString(CultureInfo.InvariantCulture)} is not in the queue."));

    internal static QueueResult InvalidTransition(long itemId, ItemState state, ItemTrigger trigger) =>
        new(new EngineMessage(
            QueueCodes.InvalidTransition,
            Args(("itemId", itemId.ToString(CultureInfo.InvariantCulture)), ("state", state.ToString()), ("trigger", trigger.ToString())),
            $"Item {itemId.ToString(CultureInfo.InvariantCulture)} cannot {trigger} while {state}."));

    internal static QueueResult NotRunning(long itemId, ItemState state) =>
        new(new EngineMessage(
            QueueCodes.NotRunning,
            Args(("itemId", itemId.ToString(CultureInfo.InvariantCulture)), ("state", state.ToString())),
            $"Item {itemId.ToString(CultureInfo.InvariantCulture)} is not running ({state})."));

    internal static QueueResult QueuePaused() =>
        new(new EngineMessage(QueueCodes.QueuePaused, "The queue is paused."));

    internal static QueueResult InvalidMove(string message) =>
        new(new EngineMessage(QueueCodes.InvalidMove, message));

    private static Dictionary<string, string> Args(params ReadOnlySpan<(string Key, string Value)> args)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }
}

/// <summary>Message codes for rejected queue commands.</summary>
public static class QueueCodes
{
    public const string ItemNotFound = "queue.itemNotFound";
    public const string InvalidTransition = "queue.invalidTransition";
    public const string InvalidMove = "queue.invalidMove";
    public const string NotRunning = "queue.notRunning";
    public const string QueuePaused = "queue.paused";
}

/// <summary>Values of <c>item_events.reason</c> for transitions the queue itself causes. Job failures use their error code.</summary>
public static class ItemEventReasons
{
    public const string Added = "added";
    public const string UserPause = "user.pause";
    public const string UserResume = "user.resume";
    public const string UserRetry = "user.retry";
    public const string UserRemove = "user.remove";
    public const string QueuePaused = "queue.paused";

    /// <summary>The engine stopped while the item was running (crash recovery, PLAN.md §5.1).</summary>
    public const string Interrupted = "engine.interrupted";

    public const string JobStarted = "job.started";
    public const string MetadataResolved = "job.metadataResolved";
    public const string PostProcessing = "job.postProcessing";
    public const string Completed = "job.completed";
    public const string InArchive = "job.inArchive";
    public const string RetryDue = "retry.due";
    public const string AuthRestored = "auth.restored";
    public const string ToolFixed = "tool.fixed";
}
