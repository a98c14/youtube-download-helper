using System.Reflection;

namespace YoutubeDownloadHelper.Engine.Hosting;

public static class EngineVersion
{
    /// <summary>The engine's version without build metadata, e.g. <c>1.0.0</c> (not <c>1.0.0+&lt;commit&gt;</c>).</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var version = typeof(EngineVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(EngineVersion).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}
