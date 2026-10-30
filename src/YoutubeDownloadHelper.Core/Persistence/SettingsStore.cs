using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>
/// Typed access to the <c>settings</c> table: one row per <see cref="EngineSettings"/> property, keyed by its
/// camelCase name, holding the value as JSON. Missing rows, unknown keys and values that no longer deserialize
/// fall back to the defaults.
/// </summary>
public sealed class SettingsStore(IDbContextFactory<StateDbContext> contextFactory, EngineSettings defaults, TimeProvider time)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        RespectNullableAnnotations = true,
    };

    public EngineSettings Defaults => defaults;

    public async Task<EngineSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Settings.AsNoTracking().ToListAsync(cancellationToken);

        var merged = ToJson(defaults);
        foreach (var row in rows)
        {
            if (!merged.ContainsKey(row.Key) || !TryParse(row.ValueJson, out var value))
            {
                continue;
            }

            var previous = merged[row.Key]?.DeepClone();
            merged[row.Key] = value;
            if (!TryDeserialize(merged, out _))
            {
                merged[row.Key] = previous;
            }
        }

        return TryDeserialize(merged, out var settings) ? settings : defaults;
    }

    /// <summary>Stores every setting that differs from what is stored, or from the default when nothing is.</summary>
    public async Task SaveAsync(EngineSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Settings.ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken);
        var defaultJson = ToJson(defaults);
        var now = time.GetUtcNow();

        foreach (var (key, node) in ToJson(settings))
        {
            var json = node?.ToJsonString(JsonOptions) ?? "null";
            if (rows.TryGetValue(key, out var row))
            {
                if (!string.Equals(row.ValueJson, json, StringComparison.Ordinal))
                {
                    row.ValueJson = json;
                    row.UpdatedAt = now;
                }
            }
            else if (!string.Equals(defaultJson[key]?.ToJsonString(JsonOptions) ?? "null", json, StringComparison.Ordinal))
            {
                db.Settings.Add(new SettingEntity { Key = key, ValueJson = json, UpdatedAt = now });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static JsonObject ToJson(EngineSettings settings) =>
        JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();

    private static bool TryParse(string json, out JsonNode? value)
    {
        try
        {
            value = JsonNode.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            value = null;
            return false;
        }
    }

    private static bool TryDeserialize(JsonObject json, out EngineSettings settings)
    {
        try
        {
            settings = json.Deserialize<EngineSettings>(JsonOptions)!;
            return settings is not null;
        }
        catch (JsonException)
        {
            settings = null!;
            return false;
        }
    }
}
