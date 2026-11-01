using System.Text.Json;
using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Queue;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Core.Queue;

/// <summary>An immutable copy of an item, taken by the queue actor; the entity itself never leaves the actor.</summary>
public sealed record QueueItem
{
    public required long Id { get; init; }

    public required string Url { get; init; }

    public string? Extractor { get; init; }

    public string? VideoId { get; init; }

    public string? Title { get; init; }

    public string? Channel { get; init; }

    public string? ChannelId { get; init; }

    public DateOnly? UploadDate { get; init; }

    public double? DurationSeconds { get; init; }

    public string? Availability { get; init; }

    public string? LiveStatus { get; init; }

    public required string PresetId { get; init; }

    public string? Folder { get; init; }

    public bool Force { get; init; }

    public required ItemState State { get; init; }

    public long Position { get; init; }

    public int Attempt { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    public ErrorCategory? ErrorCategory { get; init; }

    public EngineMessage? Error { get; init; }

    public string? FilePath { get; init; }

    public long? SizeBytes { get; init; }

    public long? WatcherId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    internal static QueueItem From(ItemEntity e) => new()
    {
        Id = e.Id,
        Url = e.Url,
        Extractor = e.Extractor,
        VideoId = e.VideoId,
        Title = e.Title,
        Channel = e.Channel,
        ChannelId = e.ChannelId,
        UploadDate = e.UploadDate,
        DurationSeconds = e.DurationSeconds,
        Availability = e.Availability,
        LiveStatus = e.LiveStatus,
        PresetId = e.PresetId,
        Folder = e.Folder,
        Force = e.Force,
        State = e.State,
        Position = e.Position,
        Attempt = e.Attempt,
        NextAttemptAt = e.NextAttemptAt,
        ErrorCategory = e.ErrorCategory is null ? null : Enum.Parse<ErrorCategory>(e.ErrorCategory),
        Error = e.ErrorCode is null ? null : new EngineMessage(e.ErrorCode, ErrorArgs.Read(e.ErrorArgsJson), e.ErrorMessage ?? e.ErrorCode),
        FilePath = e.FilePath,
        SizeBytes = e.SizeBytes,
        WatcherId = e.WatcherId,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
    };
}

/// <summary>Reads and writes <see cref="ItemEntity.ErrorArgsJson"/>.</summary>
internal static class ErrorArgs
{
    public static string Write(IReadOnlyDictionary<string, string> args) => JsonSerializer.Serialize(args);

    public static IReadOnlyDictionary<string, string> Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return args is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(args, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
