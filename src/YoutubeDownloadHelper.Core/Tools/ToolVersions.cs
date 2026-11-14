namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Orders the versions of one tool on one channel (PLAN.md §5.6): <c>2026.10.26</c>, <c>2026.11.01.233311</c>,
/// <c>2.10.0</c>, <c>2026-11-01-1909</c>. The versions are split on <c>.</c>, <c>-</c> and <c>_</c>; numeric parts compare
/// as numbers, others ordinally, and a version that is a prefix of another comes first. Versions of different channels
/// are never compared: switching channels installs the other channel's latest, whatever its number.
/// </summary>
public sealed class ToolVersions : IComparer<string>
{
    private static readonly char[] Separators = ['.', '-', '_'];

    public static ToolVersions Comparer { get; } = new();

    public static bool IsNewer(string candidate, string current) => Comparer.Compare(candidate, current) > 0;

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        var left = x.Split(Separators);
        var right = y.Split(Separators);
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var result = ComparePart(left[i], right[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int ComparePart(string a, string b)
    {
        if (IsDigits(a) && IsDigits(b))
        {
            // As numbers of any length: without leading zeros, the longer one is larger.
            var x = a.TrimStart('0');
            var y = b.TrimStart('0');
            return x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
        }

        return string.CompareOrdinal(a, b);
    }

    private static bool IsDigits(string part) => part.Length > 0 && part.All(char.IsAsciiDigit);
}
