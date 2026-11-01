using System.Reflection;
using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Core.Errors;
using YoutubeDownloadHelper.Core.Tests.FakeYtDlp;

namespace YoutubeDownloadHelper.Core.Tests.Errors;

public sealed class ErrorClassifierTests
{
    private static readonly ErrorClassifier Classifier = new();

    public static TheoryData<string> FixtureFiles => [.. ErrorFixture.All.Select(f => f.File)];

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Fixtures_are_classified_as_the_manifest_says(string file)
    {
        var fixture = ErrorFixture.Get(file);

        var result = Classifier.Classify(fixture.ExitCode, fixture.ReadLines());

        Assert.Equal((fixture.Category, fixture.Code), (result.Category, result.Error.Code));
        Assert.Equal(fixture.Args.OrderBy(a => a.Key, StringComparer.Ordinal), result.Error.Args.OrderBy(a => a.Key, StringComparer.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(result.Error.Message));
        Assert.Equal(fixture.Code == ErrorCodes.DiskFull, result.IsDiskFull);
    }

    [Fact]
    public void Every_fixture_file_is_in_the_manifest_once()
    {
        var files = Directory.GetFiles(ErrorFixture.Folder, "*.stderr.txt")
            .Select(f => Path.GetFileName(f)[..^".stderr.txt".Length])
            .Order(StringComparer.Ordinal);

        Assert.Equal(files, ErrorFixture.All.Select(f => f.File).Order(StringComparer.Ordinal));
        Assert.All(ErrorFixture.All, f => Assert.False(string.IsNullOrWhiteSpace(f.Source)));
    }

    [Fact]
    public void Every_category_and_code_has_a_fixture()
    {
        var codes = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.Empty(Enum.GetValues<ErrorCategory>().Except(ErrorFixture.All.Select(f => f.Category)));
        Assert.Empty(codes.Except(ErrorFixture.All.Select(f => f.Code), StringComparer.Ordinal));
    }

    [Fact]
    public void Codes_follow_the_category_naming()
    {
        foreach (var fixture in ErrorFixture.All)
        {
            var name = fixture.Category.ToString();
            Assert.Equal(char.ToLowerInvariant(name[0]) + name[1..], fixture.Code.Split('.')[1]);
        }
    }

    [Fact]
    public void Matching_ignores_case_without_the_current_culture()
    {
        // Under tr-TR a culture-aware comparison does not equate "I" and "i" ("I" lowercases to "ı").
        var result = Classifier.Classify(1, ["ERROR: [YOUTUBE] X: PRIVATE VIDEO. SIGN IN IF YOU'VE BEEN GRANTED ACCESS TO THIS VIDEO."]);

        Assert.Equal(ErrorCodes.Private, result.Error.Code);
    }

    [Fact]
    public void Most_patterns_look_at_error_lines_only()
    {
        var result = Classifier.Classify(1, ["WARNING: [youtube] x: Private video", "ERROR: unable to download video data: HTTP Error 503: Service Unavailable"]);

        Assert.Equal(ErrorCodes.ServerError, result.Error.Code);
    }

    [Fact]
    public void Pattern_order_wins_over_line_order()
    {
        var result = Classifier.Classify(1, ["ERROR: unable to download video data: HTTP Error 503: Service Unavailable", "ERROR: [youtube] x: Private video"]);

        Assert.Equal(ErrorCodes.Private, result.Error.Code);
    }

    [Fact]
    public void A_crash_exit_code_wins_over_the_output()
    {
        var result = Classifier.Classify(unchecked((int)0xC0000005), ["ERROR: [youtube] x: Private video"]);

        Assert.Equal((ErrorCategory.ToolBroken, ErrorCodes.Crashed, "0xC0000005"), (result.Category, result.Error.Code, result.Error.Args["exitCode"]));
    }

    [Fact]
    public void A_double_quoted_path_is_unescaped()
    {
        // Python's repr() switches to double quotes when the path contains an apostrophe.
        var result = Classifier.Classify(1, ["ERROR: unable to open for writing: [Errno 13] Permission denied: \"C:\\\\Users\\\\O'Brien\\\\a.part\""]);

        Assert.Equal("C:\\Users\\O'Brien\\a.part", result.Error.Args["path"]);
    }

    /// <summary>The fake's failure scenarios (its README) classify like the fixtures they were written from.</summary>
    [Theory]
    [InlineData("http-429", ErrorCodes.RateLimited)]
    [InlineData("http-5xx?status=502", ErrorCodes.ServerError)]
    [InlineData("connection-reset", ErrorCodes.Network)]
    [InlineData("disk-full", ErrorCodes.DiskFull)]
    [InlineData("access-denied", ErrorCodes.AccessDenied)]
    [InlineData("members-only", ErrorCodes.MembersOnly)]
    [InlineData("premium-only", ErrorCodes.PremiumOnly)]
    [InlineData("age-restricted", ErrorCodes.AgeRestricted)]
    [InlineData("bot-check", ErrorCodes.BotCheck)]
    [InlineData("private", ErrorCodes.Private)]
    [InlineData("removed", ErrorCodes.Removed)]
    [InlineData("geo-blocked", ErrorCodes.GeoBlocked)]
    [InlineData("extractor-failure", ErrorCodes.Extractor)]
    [InlineData("nsig", ErrorCodes.Signature)]
    [InlineData("upcoming", ErrorCodes.Upcoming)]
    [InlineData("unknown-error", ErrorCodes.Unknown)]
    public async Task FakeYtDlp_failures_are_classified(string scenario, string code)
    {
        using var folder = new FakeYtDlpFolder();

        var run = await FakeYtDlpProcess.RunAsync([.. FakeYtDlpProcess.EngineArgs(folder.Temp, folder.Home), "https://fake.test/" + scenario]);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal(code, Classifier.Classify(run.ExitCode, [.. run.Stdout, .. run.Stderr]).Error.Code);
    }
}
