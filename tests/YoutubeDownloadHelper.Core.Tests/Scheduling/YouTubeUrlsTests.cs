using YoutubeDownloadHelper.Core.Scheduling;

namespace YoutubeDownloadHelper.Core.Tests.Scheduling;

public sealed class YouTubeUrlsTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?feature=share&v=dQw4w9WgXcQ&list=PL123", "dQw4w9WgXcQ")]
    [InlineData("http://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=42", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/a-b_c123456", "a-b_c123456")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://WWW.YOUTUBE.COM/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/playlist?list=PL123", null)]
    [InlineData("https://www.youtube.com/@channel/videos", null)]
    [InlineData("https://www.youtube.com/watch?v=tooShort", null)]
    [InlineData("https://www.youtube.com/watch?V=dQw4w9WgXcQ", null)]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://fake.test/success?id=dQw4w9WgXcQ", null)]
    [InlineData("ftp://youtu.be/dQw4w9WgXcQ", null)]
    [InlineData("not a url", null)]
    public void TryGetVideoId_reads_single_video_urls(string url, string? expected) =>
        Assert.Equal(expected, YouTubeUrls.TryGetVideoId(url));

    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("a-b_c123456", true)]
    [InlineData("dQw4w9WgXc", false)]
    [InlineData("dQw4w9WgXcQQ", false)]
    [InlineData("dQw4w9WgXc!", false)]
    [InlineData("dQw4w9WgXcİ", false)]
    [InlineData(null, false)]
    public void IsVideoId_takes_11_url_safe_characters(string? id, bool expected) =>
        Assert.Equal(expected, YouTubeUrls.IsVideoId(id));
}
