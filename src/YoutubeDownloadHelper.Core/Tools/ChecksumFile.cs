namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>Reads published SHA-256 checksum files.</summary>
public static class ChecksumFile
{
    private const int HashLength = 64;

    /// <summary>
    /// The SHA-256 of <paramref name="assetName"/>, lower-case hex, or <see langword="null"/> when the file does not
    /// list it. Understands <c>sha256sum</c> lines (<c>&lt;hash&gt;  &lt;name&gt;</c>, also <c>*&lt;name&gt;</c>), as
    /// yt-dlp and FFmpeg-Builds publish them, and PowerShell's <c>Get-FileHash</c> list (<c>Hash : &lt;HASH&gt;</c>),
    /// as Deno publishes one per asset; that format names no file, so it only counts when it holds a single hash.
    /// </summary>
    public static string? FindSha256(string text, string assetName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(assetName);

        var powerShellHashes = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > HashLength && IsHash(line.AsSpan(0, HashLength)) && char.IsWhiteSpace(line[HashLength]))
            {
                var name = line[HashLength..].TrimStart().TrimStart('*');
                if (string.Equals(name, assetName, StringComparison.Ordinal))
                {
                    return line[..HashLength].ToLowerInvariant();
                }

                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Hash", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(colon + 1)..].Trim();
                if (value.Length == HashLength && IsHash(value))
                {
                    powerShellHashes.Add(value.ToLowerInvariant());
                }
            }
        }

        return powerShellHashes.Count == 1 ? powerShellHashes[0] : null;
    }

    private static bool IsHash(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
