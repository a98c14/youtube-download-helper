namespace YoutubeDownloadHelper.Contracts.Tools;

/// <summary>One managed tool.</summary>
/// <param name="Tool">One of <see cref="ToolNames"/>.</param>
/// <param name="Version">The current version, or <see langword="null"/> when it is not known.</param>
/// <param name="Installed">The tool's binary is present.</param>
public sealed record ToolInfo(string Tool, string? Version, bool Installed);
