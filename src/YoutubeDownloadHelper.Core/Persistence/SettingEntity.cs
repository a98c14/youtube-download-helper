namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>One setting as JSON (table <c>settings</c>). Read and written through <see cref="SettingsStore"/>.</summary>
public sealed class SettingEntity
{
    public required string Key { get; set; }

    public required string ValueJson { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
