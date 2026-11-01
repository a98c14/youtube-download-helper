using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using YoutubeDownloadHelper.Contracts.Messages;

namespace YoutubeDownloadHelper.Engine.Tests;

/// <summary>The engine's JSON conventions (camelCase, enums as camelCase strings), for test clients.</summary>
public static class TestJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T Read<T>(JsonElement element) => element.Deserialize<T>(Options)!;

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return (await response.Content.ReadFromJsonAsync<T>(Options, TestContext.Current.CancellationToken))!;
    }

    public static Task<EngineMessage> ReadErrorAsync(HttpResponseMessage response) => ReadAsync<EngineMessage>(response);
}
