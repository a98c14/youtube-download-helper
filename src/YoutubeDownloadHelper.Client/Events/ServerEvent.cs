using System.Collections.Frozen;
using System.Text.Json;
using YoutubeDownloadHelper.Contracts.Auth;
using YoutubeDownloadHelper.Contracts.Events;

namespace YoutubeDownloadHelper.Client.Events;

/// <summary>One event of the engine's event stream (PLAN.md §6.3).</summary>
/// <param name="Id">The engine's event sequence number; increases, with gaps.</param>
/// <param name="Type">The event name, one of <see cref="EventTypes"/> (or a newer one this client does not know).</param>
/// <param name="Data">The event's data: one JSON object.</param>
public sealed record ServerEvent(long Id, string Type, string Data)
{
    private static readonly FrozenDictionary<string, Type> DataTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [EventTypes.ItemAdded] = typeof(ItemAddedData),
        [EventTypes.ItemUpdated] = typeof(ItemUpdatedData),
        [EventTypes.ItemProgress] = typeof(ItemProgressData),
        [EventTypes.ItemRemoved] = typeof(ItemRemovedData),
        [EventTypes.QueueReordered] = typeof(QueueReorderedData),
        [EventTypes.QueueBlocked] = typeof(QueueBlockData),
        [EventTypes.QueueUnblocked] = typeof(QueueBlockData),
        [EventTypes.ToolsChanged] = typeof(ToolsChangedData),
        [EventTypes.ToolInstall] = typeof(ToolInstallData),
        [EventTypes.AuthChanged] = typeof(AuthChangedData),
        [EventTypes.Notify] = typeof(NotifyData),
        [EventTypes.EngineStopping] = typeof(EngineStoppingData),
        [EventTypes.Resync] = typeof(ResyncData),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The data record of <paramref name="type"/> (see <see cref="EventTypes"/>), or null for an unknown event.</summary>
    public static Type? DataTypeOf(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return DataTypes.GetValueOrDefault(type);
    }

    /// <summary>The data as <typeparamref name="T"/>.</summary>
    /// <exception cref="JsonException">The data is not a <typeparamref name="T"/>.</exception>
    public T GetData<T>() =>
        JsonSerializer.Deserialize<T>(Data, EngineJson.Options) ?? throw new JsonException($"The {Type} event has no data.");

    /// <summary>The data as the record <see cref="EventTypes"/> names for <see cref="Type"/>, or null for an unknown event.</summary>
    /// <exception cref="JsonException">The data does not match its record.</exception>
    public object? GetData() =>
        DataTypeOf(Type) is { } type
            ? JsonSerializer.Deserialize(Data, type, EngineJson.Options) ?? throw new JsonException($"The {Type} event has no data.")
            : null;
}
