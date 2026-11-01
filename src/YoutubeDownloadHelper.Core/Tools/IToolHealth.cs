namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Whether the managed tools can run downloads. While they can't, the scheduler raises the <c>ToolsMissing</c> queue
/// block (PLAN.md §4.3). <see cref="AlwaysReadyToolHealth"/> is the default; the tool manager (#43) replaces it with
/// its install and health-check state, and calls <c>Scheduler.Wake()</c> when that changes.
/// </summary>
public interface IToolHealth
{
    bool ToolsReady { get; }
}

/// <summary>Always ready: nothing installs or checks the tools yet.</summary>
public sealed class AlwaysReadyToolHealth : IToolHealth
{
    public bool ToolsReady => true;
}
