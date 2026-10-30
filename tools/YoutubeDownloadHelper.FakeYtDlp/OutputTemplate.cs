using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YoutubeDownloadHelper.FakeYtDlp;

/// <summary>
/// A small subset of yt-dlp's output template language: <c>%(field)s</c>, <c>%(field)d</c>,
/// <c>%(upload_date&gt;%Y-%m-%d)s</c> and <c>%%</c>. Missing fields render as <c>NA</c>.
/// </summary>
internal static partial class OutputTemplate
{
    /// <summary>yt-dlp's Windows filename sanitization: forbidden characters become full-width look-alikes.</summary>
    private static readonly Dictionary<char, char> FullWidth = new()
    {
        ['/'] = '⧸', ['\\'] = '⧹', [':'] = '：', ['*'] = '＊', ['?'] = '？', ['"'] = '＂', ['<'] = '＜', ['>'] = '＞',
        ['|'] = '｜',
    };

    public static string Render(string template, IReadOnlyDictionary<string, string> fields, bool sanitize) =>
        Field().Replace(template, m =>
        {
            if (m.Value == "%%")
            {
                return "%";
            }

            var value = fields.GetValueOrDefault(m.Groups["name"].Value);
            if (value is null)
            {
                return "NA";
            }

            if (m.Groups["fmt"].Success &&
                DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                value = m.Groups["fmt"].Value
                    .Replace("%Y", date.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("%m", date.ToString("MM", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("%d", date.ToString("dd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            }

            return sanitize ? Sanitize(value) : value;
        });

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(FullWidth.TryGetValue(c, out var replacement) ? replacement : c < ' ' ? ' ' : c);
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"%%|%\((?<name>[^)>]+)(?:>(?<fmt>[^)]+))?\)[sd]")]
    private static partial Regex Field();
}
