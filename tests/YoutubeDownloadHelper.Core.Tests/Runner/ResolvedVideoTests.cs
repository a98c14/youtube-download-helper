using YoutubeDownloadHelper.Core.Runner;

namespace YoutubeDownloadHelper.Core.Tests.Runner;

public sealed class ResolvedVideoTests
{
    [Fact]
    public void Fallback_fields_are_used_when_the_main_ones_are_missing()
    {
        var video = ResolvedVideo.Parse("""
            {"id": "abc", "extractor": "Youtube", "fulltitle": "\u0130zmir", "uploader": "Kanal", "filesize_approx": 1234.5,
             "_filename": "C:\\x\\y.webm", "upload_date": "2026013", "duration": 0}
            """);

        Assert.Equal(new ResolvedVideo("youtube", "abc", "İzmir", "Kanal", null, null, null, null, null, 1234, @"C:\x\y.webm"), video);
    }

    [Fact]
    public void The_exact_size_wins_over_the_estimate()
    {
        var video = ResolvedVideo.Parse("""{"id": "abc", "extractor_key": "Youtube", "filesize": 10, "filesize_approx": 20, "duration": 1.5}""");

        Assert.Equal(10, video?.SizeEstimate);
        Assert.Equal(TimeSpan.FromSeconds(1.5), video?.Duration);
    }

    [Theory]
    [InlineData("""{"_type": "playlist", "id": "PL1", "entries": []}""")]
    [InlineData("""{"title": "no id"}""")]
    [InlineData("""[1]""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public void Anything_but_a_single_video_is_null(string json) => Assert.Null(ResolvedVideo.Parse(json));
}
