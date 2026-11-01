namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Where the managed tools are. <see cref="ConfiguredToolLocator"/> is the default; the tool manager (#43) replaces
/// it with one that returns the current installed versions.
/// </summary>
public interface IToolLocator
{
    /// <summary>The tools a new run should use.</summary>
    ToolPaths Current { get; }
}
