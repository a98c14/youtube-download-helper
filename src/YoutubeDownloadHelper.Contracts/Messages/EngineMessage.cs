using System.Text.Json.Serialization;

namespace YoutubeDownloadHelper.Contracts.Messages;

/// <summary>
/// User-facing information from the engine (PLAN.md §6.3, §7.5). Clients localize from <see cref="Code"/> and
/// <see cref="Args"/>; <see cref="Message"/> is an English fallback for third-party clients. Also the body of every
/// API error response.
/// </summary>
public sealed record EngineMessage
{
    /// <param name="code">A stable identifier, e.g. <c>error.unavailable.private</c>.</param>
    /// <param name="args">The parameters the localized text is formatted with.</param>
    /// <param name="message">English fallback text.</param>
    [JsonConstructor]
    public EngineMessage(string code, IReadOnlyDictionary<string, string> args, string message)
    {
        Code = code;
        Args = args;
        Message = message;
    }

    public EngineMessage(string code, string message)
        : this(code, new Dictionary<string, string>(StringComparer.Ordinal), message)
    {
    }

    /// <summary>A stable identifier, e.g. <c>error.unavailable.private</c>.</summary>
    public string Code { get; init; }

    /// <summary>The parameters the localized text is formatted with.</summary>
    public IReadOnlyDictionary<string, string> Args { get; init; }

    /// <summary>English fallback text.</summary>
    public string Message { get; init; }
}
