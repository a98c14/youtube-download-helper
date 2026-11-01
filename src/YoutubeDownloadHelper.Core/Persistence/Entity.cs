namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>
/// The columns every table has. <see cref="TimestampInterceptor"/> fills the timestamps on save, so callers do not
/// set them.
/// </summary>
public abstract class Entity
{
    /// <summary>Assigned by SQLite on insert. <c>AUTOINCREMENT</c>, so an id is never reused, even after a delete.</summary>
    public long Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
