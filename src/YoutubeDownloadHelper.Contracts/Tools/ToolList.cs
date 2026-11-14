using YoutubeDownloadHelper.Contracts.Settings;

namespace YoutubeDownloadHelper.Contracts.Tools;

/// <summary>The managed tools in detail (<c>GET</c> <see cref="ApiRoutes.Tools"/>, PLAN.md §5.6).</summary>
/// <param name="Tools">In <see cref="ToolNames"/> order: yt-dlp, ffmpeg, Deno.</param>
/// <param name="Ready">Every tool is installed and works; downloads can run.</param>
/// <param name="YtDlpChannel">
/// The configured yt-dlp channel. Right after a switch it can differ from yt-dlp's <see cref="ToolDetails.Channel"/>
/// until the other channel's build is installed.
/// </param>
public sealed record ToolList(IReadOnlyList<ToolDetails> Tools, bool Ready, YtDlpChannel YtDlpChannel);

/// <summary>One managed tool.</summary>
/// <param name="Tool">One of <see cref="ToolNames"/>.</param>
/// <param name="Version">The current version; null when not installed or not managed.</param>
/// <param name="Installed">The tool's binaries are present.</param>
/// <param name="Managed">
/// The engine installs and updates it. False for a tool with a path override in the engine configuration (development).
/// </param>
/// <param name="Channel">The current version's channel; yt-dlp only.</param>
/// <param name="PreviousVersion">The version a rollback (<see cref="ApiRoutes.ToolRollback"/>) goes back to, if any.</param>
/// <param name="LastCheck">When the release feed was last checked for this tool.</param>
public sealed record ToolDetails(
    string Tool,
    string? Version,
    bool Installed,
    bool Managed,
    YtDlpChannel? Channel,
    string? PreviousVersion,
    DateTimeOffset? LastCheck);
