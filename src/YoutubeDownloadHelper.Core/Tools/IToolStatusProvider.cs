using YoutubeDownloadHelper.Contracts.Tools;

namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// The tools as <c>GET /info</c> reports them. <see cref="LocatorToolStatusProvider"/> is the default; the tool
/// manager (#43) replaces it with one that knows the installed versions and the health check result.
/// </summary>
public interface IToolStatusProvider
{
    IReadOnlyList<ToolInfo> GetTools();
}

/// <summary>Whether each <see cref="IToolLocator"/> path exists. Versions are unknown.</summary>
public sealed class LocatorToolStatusProvider(IToolLocator locator) : IToolStatusProvider
{
    public IReadOnlyList<ToolInfo> GetTools()
    {
        var paths = locator.Current;
        return
        [
            new ToolInfo(ToolNames.YtDlp, null, File.Exists(paths.YtDlp)),
            new ToolInfo(ToolNames.Ffmpeg, null, File.Exists(paths.Ffmpeg) || Directory.Exists(paths.Ffmpeg)),
            new ToolInfo(ToolNames.Deno, null, File.Exists(paths.Deno)),
        ];
    }
}
