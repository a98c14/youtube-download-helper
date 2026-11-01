namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// The engine's configuration section <c>Engine</c> (command line <c>--Engine:Port=0</c>, environment variable
/// <c>Engine__Port</c>, or <c>appsettings.json</c> next to the executable). Everything is optional; the user's
/// settings live in the database. Tool overrides are bound from <c>Engine:Tools</c> (<c>ToolLocatorOptions</c>).
/// </summary>
public sealed class EngineOptions
{
    public const string Section = "Engine";

    public const string ToolsSection = Section + ":Tools";

    /// <summary>
    /// Overrides the <c>ApiPort</c> setting. <c>0</c> picks a dynamic port. Either way, a port that is taken falls
    /// back to a dynamic one.
    /// </summary>
    public int? Port { get; set; }

    /// <summary>Overrides the data folder (development and tests). The single-instance scope follows it.</summary>
    public string? DataRoot { get; set; }
}
