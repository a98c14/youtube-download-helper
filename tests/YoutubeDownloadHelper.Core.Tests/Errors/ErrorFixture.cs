using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YoutubeDownloadHelper.Contracts.Errors;

namespace YoutubeDownloadHelper.Core.Tests.Errors;

/// <summary>
/// One entry of <c>Errors/Fixtures/fixtures.json</c>: a yt-dlp output sample (<c>&lt;File&gt;.stderr.txt</c>), how the
/// classifier must read it, and where the sample came from.
/// </summary>
public sealed record ErrorFixture(
    string File,
    int ExitCode,
    ErrorCategory Category,
    string Code,
    Dictionary<string, string> Args,
    string Source)
{
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "Errors", "Fixtures");

    public static IReadOnlyList<ErrorFixture> All { get; } = Load();

    public static ErrorFixture Get(string file) => All.Single(f => f.File == file);

    /// <summary>The sample split on <c>\n</c> only, so a CRLF capture keeps its <c>\r</c> for the classifier to handle.</summary>
    public string[] ReadLines()
    {
        var text = System.IO.File.ReadAllText(Path.Combine(Folder, File + ".stderr.txt"), new UTF8Encoding(false));
        return text.Length == 0 ? [] : text.TrimEnd('\n').Split('\n');
    }

    private static List<ErrorFixture> Load()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        using var stream = System.IO.File.OpenRead(Path.Combine(Folder, "fixtures.json"));
        return JsonSerializer.Deserialize<List<ErrorFixture>>(stream, options)!;
    }
}
