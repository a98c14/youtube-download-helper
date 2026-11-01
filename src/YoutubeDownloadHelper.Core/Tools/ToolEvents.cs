using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Core.Events;

namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>A step of an install (<see cref="ToolInstallProgressEvent"/>).</summary>
public enum ToolInstallPhase
{
    Downloading,
    Verifying,
    Extracting,
    Testing,
    Switching,
}

/// <summary>
/// An install is under way (PLAN.md §5.6; the first-run wizard shows it). While <see cref="ToolInstallPhase.Downloading"/>,
/// published at most every <see cref="ToolInstaller.ProgressInterval"/>; <paramref name="TotalBytes"/> is null when the
/// size is not known.
/// </summary>
public sealed record ToolInstallProgressEvent(
    string Tool, string Version, ToolInstallPhase Phase, long BytesReceived, long? TotalBytes) : EngineEvent;

/// <summary>A version was installed and is now the tool's current one.</summary>
/// <param name="Tool">One of <c>ToolNames</c>.</param>
/// <param name="Version">The new current version.</param>
/// <param name="PreviousVersion">The version it replaced, or <see langword="null"/> on a first install.</param>
public sealed record ToolInstalledEvent(string Tool, string Version, string? PreviousVersion) : EngineEvent;

/// <summary>An install failed; the current version (if any) is unchanged. The tool manager retries later.</summary>
public sealed record ToolInstallFailedEvent(string Tool, EngineMessage Reason) : EngineEvent;

/// <summary>
/// <see cref="IToolHealth.ToolsReady"/> changed. The scheduler wakes on every event, so this also makes it raise or
/// lift the <c>ToolsMissing</c> queue block.
/// </summary>
public sealed record ToolHealthChangedEvent(bool ToolsReady) : EngineEvent;
