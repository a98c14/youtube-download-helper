using System.Collections;
using System.Text;

namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>
/// The command line and environment block for <c>CreateProcessW</c>. Quoting follows the rules
/// <c>CommandLineToArgvW</c> and the MSVC runtime parse with (the same as <c>ProcessStartInfo.ArgumentList</c>), so
/// every argument reaches the child exactly as given.
/// </summary>
public static class WindowsCommandLine
{
    /// <summary>The quoted executable followed by each argument, quoted only where needed.</summary>
    public static string Build(string fileName, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        // The program name is parsed differently (no backslash escapes), and a path can't contain a quote.
        var builder = new StringBuilder().Append('"').Append(fileName).Append('"');
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            Append(builder, argument);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The variables a child gets: <paramref name="variables"/> without any <c>PYTHON*</c> variable (spike #33: the
    /// user's Python setup must never change how <c>yt-dlp.exe</c> behaves).
    /// </summary>
    public static SortedDictionary<string, string> ChildEnvironment(IDictionary variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        // Windows wants the block sorted case-insensitively, and names are case-insensitive.
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in variables)
        {
            if (entry.Key is string name && name.Length > 0
                && !name.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase))
            {
                result[name] = entry.Value as string ?? "";
            }
        }

        return result;
    }

    /// <summary>A <c>CREATE_UNICODE_ENVIRONMENT</c> block: <c>name=value\0</c> per variable, then <c>\0</c>.</summary>
    public static string EnvironmentBlock(IEnumerable<KeyValuePair<string, string>> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var builder = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            builder.Append(name).Append('=').Append(value).Append('\0');
        }

        if (builder.Length == 0)
        {
            builder.Append('\0');
        }

        return builder.Append('\0').ToString();
    }

    private static void Append(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Backslashes before a quote are escapes: double them, then escape the quote itself.
                builder.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                builder.Append('\\', backslashes);
            }

            backslashes = 0;
            builder.Append(c);
        }

        // Backslashes before the closing quote are doubled too.
        builder.Append('\\', backslashes * 2).Append('"');
    }
}
