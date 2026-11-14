using System.Text.Json;
using System.Text.Json.Serialization;

namespace YoutubeDownloadHelper.Client;

/// <summary>The engine's JSON conventions (PLAN.md §6): camelCase, enums as camelCase strings, numbers never as strings.</summary>
public static class EngineJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,

            // Request fields that are not set stay out of the body (e.g. a settings patch changes only what it names).
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
