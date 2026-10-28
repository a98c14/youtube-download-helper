using System.Globalization;

namespace YoutubeDownloadHelper.Tests.Shared;

/// <summary>
/// Linked into the test projects that also run under <c>--culture tr-TR</c>. The tr-TR run sets
/// <c>EXPECTED_TEST_CULTURE</c>, so this fails if the culture switch silently stops working.
/// </summary>
public sealed class CultureCanaryTests
{
    [Fact]
    public void Current_culture_matches_EXPECTED_TEST_CULTURE()
    {
        var expected = Environment.GetEnvironmentVariable("EXPECTED_TEST_CULTURE");
        if (string.IsNullOrEmpty(expected))
        {
            return;
        }

        Assert.Equal(expected, CultureInfo.CurrentCulture.Name);
        Assert.Equal(expected, CultureInfo.CurrentUICulture.Name);
    }
}
