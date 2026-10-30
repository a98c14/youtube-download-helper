using System.Globalization;

namespace YoutubeDownloadHelper.FakeYtDlp;

/// <summary>
/// The scenario and parameters encoded in the URL: <c>https://fake.test/&lt;scenario&gt;[/&lt;sub&gt;]?param=value</c>.
/// <c>youtube.com/watch?v=…</c> and <c>youtu.be/…</c> map to <c>success</c> with that id, and
/// <c>youtube.com/feed/subscriptions</c> to <c>subscriptions</c>, so real URLs work without wiring.
/// </summary>
internal sealed class FakeRequest
{
    private readonly Dictionary<string, string> _query;

    private FakeRequest(string url, string scenario, string? sub, Dictionary<string, string> query)
    {
        Url = url;
        Scenario = scenario;
        Sub = sub;
        _query = query;
    }

    public string Url { get; }

    public string Scenario { get; }

    public string? Sub { get; }

    public static FakeRequest? Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var query = ParseQuery(uri.Query);
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var host = uri.Host.ToLowerInvariant();
        switch (host)
        {
            case "fake.test" when segments.Length > 0:
                return new FakeRequest(url, segments[0], segments.Length > 1 ? segments[1] : null, query);
            case "www.youtube.com" or "youtube.com" or "m.youtube.com":
                if (segments is ["watch"] && query.ContainsKey("v"))
                {
                    query["id"] = query["v"];
                    return new FakeRequest(url, "success", null, query);
                }

                return segments is ["feed", "subscriptions"] ? new FakeRequest(url, "subscriptions", null, query) : null;
            case "youtu.be" when segments.Length == 1:
                query["id"] = segments[0];
                return new FakeRequest(url, "success", null, query);
            default:
                return null;
        }
    }

    public string? Text(string name) => _query.GetValueOrDefault(name);

    public string Text(string name, string fallback) => _query.GetValueOrDefault(name) ?? fallback;

    public bool Has(string name) => _query.ContainsKey(name);

    /// <summary>A byte count; accepts <c>K</c>, <c>M</c> and <c>G</c> suffixes (binary).</summary>
    public long Bytes(string name, long fallback)
    {
        var text = Text(name);
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        var multiplier = char.ToUpperInvariant(text[^1]) switch
        {
            'K' => 1L << 10,
            'M' => 1L << 20,
            'G' => 1L << 30,
            _ => 1L,
        };
        var number = multiplier == 1 ? text : text[..^1];
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, (long)(value * multiplier))
            : fallback;
    }

    public double Number(string name, double fallback) =>
        double.TryParse(Text(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }
}
