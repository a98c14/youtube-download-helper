namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>A channel or playlist subscription (table <c>watchers</c>, PLAN.md §5.5). Behaviour lands in #58.</summary>
public sealed class WatcherEntity
{
    public const int DefaultIntervalMinutes = 60;

    public Guid Id { get; set; }

    public required string Url { get; set; }

    public string? ChannelId { get; set; }

    public string? Title { get; set; }

    /// <summary><c>Channel</c> or <c>Playlist</c> (#58 owns the enum).</summary>
    public required string Kind { get; set; }

    public bool IncludeVideos { get; set; } = true;

    public bool IncludeShorts { get; set; }

    public bool IncludePastStreams { get; set; }

    public bool IncludeMembersOnly { get; set; }

    public required string PresetId { get; set; }

    public string? FolderOverride { get; set; }

    public int IntervalMinutes { get; set; } = DefaultIntervalMinutes;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary><c>None</c> or <c>LastN</c> (#58 owns the enum).</summary>
    public string Backfill { get; set; } = "None";

    /// <summary>N for <c>LastN</c>.</summary>
    public int? BackfillCount { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>The members tab failed with an auth error on the last check.</summary>
    public bool NeedsLogin { get; set; }

    public string AccountId { get; set; } = Accounts.Default;

    public List<WatcherSeenEntity> Seen { get; } = [];
}
