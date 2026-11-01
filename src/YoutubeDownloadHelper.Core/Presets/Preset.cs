using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Contracts.Presets;

namespace YoutubeDownloadHelper.Core.Presets;

/// <summary>A quality preset: its ID, its localizable name and the yt-dlp format arguments behind it (PLAN.md §5.3).</summary>
/// <param name="Id">One of <see cref="PresetIds"/>.</param>
/// <param name="NameCode">One of <see cref="PresetCodes"/>.</param>
/// <param name="EnglishName">The English fallback of the name.</param>
/// <param name="AudioOnly">The preset produces an audio file.</param>
/// <param name="FormatArguments">
/// Format selection, sort and merge/convert options, one <c>ArgumentList</c> entry each. Empty for
/// <see cref="PresetIds.Best"/>.
/// </param>
public sealed record Preset(string Id, string NameCode, string EnglishName, bool AudioOnly, IReadOnlyList<string> FormatArguments)
{
    public PresetInfo ToInfo() => new(Id, new EngineMessage(NameCode, EnglishName), AudioOnly);
}
