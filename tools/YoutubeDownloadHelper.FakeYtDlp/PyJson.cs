using System.Collections;
using System.Globalization;
using System.Text;

namespace YoutubeDownloadHelper.FakeYtDlp;

/// <summary>An ordered JSON object. Null values are skipped, like the missing keys of yt-dlp's templates.</summary>
internal sealed class PyDict : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly List<KeyValuePair<string, object?>> _items = [];

    public void Add(string key, object? value)
    {
        if (value is not null)
        {
            _items.Add(new(key, value));
        }
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Writes JSON the way Python's <c>json.dumps</c> does with its defaults, which is what yt-dlp's <c>%(…)j</c> and
/// <c>-J</c> produce: <c>", "</c> and <c>": "</c> separators, everything outside printable ASCII as lowercase
/// <c>\uXXXX</c>, and floats with a fractional part (<c>22784.0</c>).
/// </summary>
internal static class PyJson
{
    public static string Dumps(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case int or long:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(FormatFloat(d));
                break;
            case PyDict dict:
                sb.Append('{');
                var first = true;
                foreach (var (key, item) in dict)
                {
                    sb.Append(first ? "" : ", ");
                    first = false;
                    WriteString(sb, key);
                    sb.Append(": ");
                    Write(sb, item);
                }

                sb.Append('}');
                break;
            case IEnumerable list:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in list)
                {
                    sb.Append(firstItem ? "" : ", ");
                    firstItem = false;
                    Write(sb, item);
                }

                sb.Append(']');
                break;
            default:
                throw new ArgumentException($"Unsupported JSON value type {value.GetType()}.", nameof(value));
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case >= ' ' and <= '~': sb.Append(c); break;
                default: sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
            }
        }

        sb.Append('"');
    }

    private static string FormatFloat(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('E', StringComparison.Ordinal))
        {
            return s.Replace("E", "e", StringComparison.Ordinal);
        }

        return s.Contains('.', StringComparison.Ordinal) ? s : s + ".0";
    }
}
