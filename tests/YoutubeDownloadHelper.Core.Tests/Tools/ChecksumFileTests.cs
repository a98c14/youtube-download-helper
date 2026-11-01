using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Core.Tests.Tools;

public sealed class ChecksumFileTests
{
    private const string Hash = "f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d";

    [Fact]
    public void Finds_the_asset_in_a_sha256sum_list()
    {
        // yt-dlp's SHA2-256SUMS: "yt-dlp" (no extension) comes before "yt-dlp.exe".
        var text = $"e51e69e89489f199530f13d32a4342da3ddb2cf34842f364603a066db05afbf3  yt-dlp\n{Hash}  yt-dlp.exe\n";

        Assert.Equal(Hash, ChecksumFile.FindSha256(text, "yt-dlp.exe"));
    }

    [Fact]
    public void Accepts_the_binary_marker_crlf_and_upper_case()
    {
        var text = $"{Hash.ToUpperInvariant()} *ffmpeg-master-latest-win64-gpl.zip\r\n";

        Assert.Equal(Hash, ChecksumFile.FindSha256(text, "ffmpeg-master-latest-win64-gpl.zip"));
    }

    [Fact]
    public void Reads_a_powershell_get_filehash_list()
    {
        // Deno's <asset>.sha256sum, verbatim apart from the hash.
        var text = $"\r\nAlgorithm : SHA256\r\nHash      : {Hash.ToUpperInvariant()}\r\nPath      : C:/a/deno/deno/target/release/deno-x86_64-pc-windows-msvc.zip\r\n\r\n";

        Assert.Equal(Hash, ChecksumFile.FindSha256(text, "deno-x86_64-pc-windows-msvc.zip"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("f5389d36  yt-dlp.exe\n")]
    [InlineData("f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d  yt-dlp_x86.exe\n")]
    [InlineData("Hash : AAAA\n")]
    public void Returns_null_without_a_checksum_for_the_asset(string text) =>
        Assert.Null(ChecksumFile.FindSha256(text, "yt-dlp.exe"));
}
