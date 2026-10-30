namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>Facts about the database file itself (table <c>schema_info</c>); keys are in <see cref="SchemaInfoKeys"/>.</summary>
public sealed class SchemaInfoEntity
{
    public required string Key { get; set; }

    public required string Value { get; set; }
}

public static class SchemaInfoKeys
{
    /// <summary>Engine version that created the file.</summary>
    public const string CreatedByVersion = "created_by_version";

    /// <summary>Engine version that last opened it.</summary>
    public const string LastOpenedByVersion = "last_opened_by_version";
}
