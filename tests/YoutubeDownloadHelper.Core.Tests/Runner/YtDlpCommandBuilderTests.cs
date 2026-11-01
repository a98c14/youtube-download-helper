using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Contracts.Presets;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Presets;
using YoutubeDownloadHelper.Core.Runner;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Tests.Runner;

/// <summary>
/// Snapshot tests of every command line (PLAN.md §5.3, §5.4, §11). The paths are non-ASCII on purpose. Review a
/// changed snapshot as a change to what yt-dlp is told to do.
/// </summary>
public sealed class YtDlpCommandBuilderTests
{
    private const string Url = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
    private const string DownloadFolder = @"C:\Users\Kullanıcı İ\Downloads\Youtube İndirgeç";
    private const string CookiesFile = @"C:\Users\Kullanıcı İ\AppData\Local\YoutubeDownloadHelperData\work\42\cookies.txt";

    private static readonly DataPaths Paths = new(@"C:\Users\Kullanıcı İ\AppData\Local\YoutubeDownloadHelperData");

    private static readonly YtDlpCommandBuilder Builder =
        new(Paths, new ConfiguredToolLocator(Paths, new ToolLocatorOptions()), new PresetRegistry());

    public static TheoryData<string> PresetIdValues => [.. PresetRegistry.BuiltIn.Select(p => p.Id)];

    [Theory]
    [MemberData(nameof(PresetIdValues))]
    public void Download_per_preset(string presetId) =>
        Snapshot(Builder.Download(Request(presetId)), $"Download_preset_{presetId}");

    [Fact]
    public void Download_with_cookies() =>
        Snapshot(Builder.Download(Request(PresetIds.Best) with { CookiesFile = CookiesFile }));

    [Fact]
    public void Download_with_video_id_suffix() =>
        Snapshot(Builder.Download(Request(PresetIds.Best) with { AppendVideoId = true }));

    [Fact]
    public void Download_with_folder_override() =>
        Snapshot(Builder.Download(Request(PresetIds.P1080) with { FolderOverride = @"D:\Arşiv\Podcast İ" }));

    [Fact]
    public void Download_with_relative_folder_override() =>
        Snapshot(Builder.Download(Request(PresetIds.P720) with { FolderOverride = @"Müzik\Çalma listesi" }));

    [Fact]
    public void Download_with_cookies_suffix_and_folder_override() =>
        Snapshot(Builder.Download(Request(PresetIds.AudioMp3) with
        {
            CookiesFile = CookiesFile,
            AppendVideoId = true,
            FolderOverride = @"D:\Arşiv\Podcast İ",
        }));

    [Fact]
    public void Resolve() => Snapshot(Builder.Resolve(Url));

    [Fact]
    public void Resolve_with_cookies() => Snapshot(Builder.Resolve(Url, CookiesFile));

    [Fact]
    public void Flat_listing() => Snapshot(Builder.FlatListing("https://www.youtube.com/@jawed/videos"));

    [Fact]
    public void Flat_listing_with_cookies() =>
        Snapshot(Builder.FlatListing("https://www.youtube.com/playlist?list=UUMOyjVIjrsBqWv3LK7ZsSpLoA", 5, CookiesFile));

    [Fact]
    public void Verify_auth() => Snapshot(Builder.VerifyAuth(CookiesFile));

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("az-Latn-AZ")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public void Output_never_depends_on_the_current_culture(string culture)
    {
        var invariant = WithCulture(CultureInfo.InvariantCulture, AllCommands);
        var other = WithCulture(CultureInfo.GetCultureInfo(culture), AllCommands);

        Assert.Equal(invariant, other);
    }

    [Fact]
    public void Download_rejects_an_unknown_preset() =>
        Assert.Throws<ArgumentException>(() => Builder.Download(Request("4k")));

    [Fact]
    public void Download_rejects_a_relative_download_folder() =>
        Assert.Throws<ArgumentException>(() => Builder.Download(Request(PresetIds.Best) with { DownloadFolder = "Downloads" }));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Flat_listing_needs_at_least_one_entry(int playlistEnd) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Builder.FlatListing(Url, playlistEnd));

    [Fact]
    public void A_url_that_looks_like_an_option_stays_a_url()
    {
        var args = Builder.Resolve("--exec=calc").Arguments;

        Assert.Equal(["--", "--exec=calc"], args.TakeLast(2));
    }

    [Fact]
    public void Each_command_takes_the_tools_the_locator_returns_at_that_moment()
    {
        var locator = new SwitchingLocator();
        var builder = new YtDlpCommandBuilder(Paths, locator, new PresetRegistry());

        var first = builder.Download(Request(PresetIds.Best));
        locator.Version = "2026.11.01";
        var second = builder.Download(Request(PresetIds.Best));

        Assert.Equal(@"C:\tools\yt-dlp\2026.10.26\yt-dlp.exe", first.FileName);
        Assert.Contains(@"deno:C:\tools\deno\2026.10.26\deno.exe", first.Arguments);
        Assert.Equal(@"C:\tools\yt-dlp\2026.11.01\yt-dlp.exe", second.FileName);
        Assert.Contains(@"deno:C:\tools\deno\2026.11.01\deno.exe", second.Arguments);
    }

    [Fact]
    public void The_builder_resolves_from_the_service_collection()
    {
        using var provider = new ServiceCollection()
            .AddPersistence(Paths, EngineSettings.Defaults(@"C:\Users\Kullanıcı İ"))
            .AddPresets()
            .AddTools()
            .AddRunner()
            .BuildServiceProvider();

        var command = provider.GetRequiredService<YtDlpCommandBuilder>().Resolve(Url);

        Assert.Equal(Path.Combine(Paths.ToolsDir, "yt-dlp", "yt-dlp.exe"), command.FileName);
    }

    private static DownloadRequest Request(string presetId) => new()
    {
        ItemId = 42,
        Url = Url,
        PresetId = presetId,
        DownloadFolder = DownloadFolder,
    };

    private static IEnumerable<YtDlpCommand> AllCommandsIn(YtDlpCommandBuilder builder) =>
    [
        .. PresetRegistry.BuiltIn.Select(p => builder.Download(Request(p.Id) with { ItemId = -1_234_567, CookiesFile = CookiesFile, AppendVideoId = true })),
        builder.Download(Request(PresetIds.Best) with { ItemId = long.MaxValue, FolderOverride = "İ" }),
        builder.Resolve(Url, CookiesFile),
        builder.FlatListing(Url, 1_000_000, CookiesFile),
        builder.VerifyAuth(CookiesFile),
    ];

    private static string AllCommands() => string.Join("\n---\n", AllCommandsIn(Builder).Select(Render));

    private static string WithCulture(CultureInfo culture, Func<string> action)
    {
        var (current, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = current;
            CultureInfo.CurrentUICulture = ui;
        }
    }

    private static void Snapshot(YtDlpCommand command, [CallerMemberName] string name = "") =>
        Approval.Verify(Render(command), name);

    /// <summary>The executable, then one argument per line, exactly as they go into <c>ArgumentList</c>.</summary>
    private static string Render(YtDlpCommand command)
    {
        var sb = new StringBuilder().Append(command.FileName).Append('\n');
        foreach (var arg in command.Arguments)
        {
            sb.Append("  ").Append(arg).Append('\n');
        }

        return sb.ToString();
    }

    private sealed class SwitchingLocator : IToolLocator
    {
        public string Version { get; set; } = "2026.10.26";

        public ToolPaths Current => new(
            $@"C:\tools\yt-dlp\{Version}\yt-dlp.exe",
            $@"C:\tools\ffmpeg\{Version}\bin",
            $@"C:\tools\deno\{Version}\deno.exe");
    }
}
