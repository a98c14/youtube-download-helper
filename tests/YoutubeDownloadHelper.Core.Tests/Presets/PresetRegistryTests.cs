using System.Reflection;
using YoutubeDownloadHelper.Contracts.Presets;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Presets;

namespace YoutubeDownloadHelper.Core.Tests.Presets;

public sealed class PresetRegistryTests
{
    private readonly PresetRegistry _registry = new();

    [Fact]
    public void Registers_the_five_presets_of_the_plan_in_order() =>
        Assert.Equal(["best", "1080p", "720p", "audio-m4a", "audio-mp3"], _registry.All.Select(p => p.Id));

    [Fact]
    public void Every_preset_id_and_code_constant_is_used_once()
    {
        Assert.Equal(Constants(typeof(PresetIds)).Order(StringComparer.Ordinal), _registry.All.Select(p => p.Id).Order(StringComparer.Ordinal));
        Assert.Equal(Constants(typeof(PresetCodes)).Order(StringComparer.Ordinal), _registry.All.Select(p => p.NameCode).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Best_is_the_default_and_uses_yt_dlp_defaults()
    {
        Assert.Equal(PresetIds.Best, _registry.Default.Id);
        Assert.Equal(PresetIds.Best, EngineSettings.Defaults(@"C:\Users\u").DefaultPresetId);
        Assert.Empty(_registry.Get(PresetIds.Best).FormatArguments);
    }

    [Fact]
    public void Only_the_audio_presets_are_audio_only() =>
        Assert.Equal([PresetIds.AudioM4a, PresetIds.AudioMp3], _registry.All.Where(p => p.AudioOnly).Select(p => p.Id));

    [Fact]
    public void Clients_see_ids_and_localizable_names()
    {
        var info = Assert.Single(_registry.Describe(), p => p.Id == PresetIds.AudioMp3);

        Assert.Equal(PresetCodes.AudioMp3, info.Name.Code);
        Assert.Empty(info.Name.Args);
        Assert.Equal("Audio (MP3)", info.Name.Message);
        Assert.True(info.AudioOnly);
        Assert.Equal(_registry.All.Select(p => p.Id), _registry.Describe().Select(p => p.Id));
    }

    [Theory]
    [InlineData("BEST")]
    [InlineData("1080P")]
    [InlineData("AUDİO-MP3")]
    [InlineData("audio")]
    [InlineData("")]
    public void Ids_are_matched_ordinally(string id)
    {
        Assert.False(_registry.Contains(id));
        Assert.False(_registry.TryGet(id, out _));
        Assert.Throws<ArgumentException>(() => _registry.Get(id));
    }

    private static IEnumerable<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);
}
