using YoutubeDownloadHelper.Contracts.Messages;

namespace YoutubeDownloadHelper.Contracts.Presets;

/// <summary>What clients see of a preset: its ID and a localizable name (a <see cref="PresetCodes"/> code).</summary>
/// <param name="Id">One of <see cref="PresetIds"/>.</param>
/// <param name="Name">The display name; <see cref="EngineMessage.Message"/> is the English fallback.</param>
/// <param name="AudioOnly">The preset produces an audio file.</param>
public sealed record PresetInfo(string Id, EngineMessage Name, bool AudioOnly);
