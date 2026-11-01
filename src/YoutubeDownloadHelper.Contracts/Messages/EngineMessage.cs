namespace YoutubeDownloadHelper.Contracts.Messages;

/// <summary>
/// User-facing information from the engine (PLAN.md §6.3, §7.5). Clients localize from <see cref="Code"/> and
/// <see cref="Args"/>; <see cref="Message"/> is an English fallback for third-party clients.
/// </summary>
/// <param name="Code">A stable identifier, e.g. <c>error.unavailable.private</c>.</param>
/// <param name="Args">The parameters the localized text is formatted with.</param>
/// <param name="Message">English fallback text.</param>
public sealed record EngineMessage(string Code, IReadOnlyDictionary<string, string> Args, string Message)
{
    public EngineMessage(string code, string message)
        : this(code, new Dictionary<string, string>(StringComparer.Ordinal), message)
    {
    }
}
