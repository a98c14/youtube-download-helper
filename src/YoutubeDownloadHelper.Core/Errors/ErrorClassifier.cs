using System.Globalization;
using YoutubeDownloadHelper.Contracts.Errors;
using YoutubeDownloadHelper.Contracts.Messages;

namespace YoutubeDownloadHelper.Core.Errors;

/// <summary>What a failed run means: its <see cref="ErrorCategory"/> and the message to show for it.</summary>
public sealed record ErrorClassification(ErrorCategory Category, EngineMessage Error)
{
    /// <summary>Disk full blocks the queue (<c>DiskFull</c>) instead of failing the item (PLAN.md §5.2).</summary>
    public bool IsDiskFull => Error.Code == ErrorCodes.DiskFull;
}

/// <summary>
/// Maps a failed yt-dlp run (exit code and output lines) to an <see cref="ErrorClassification"/> (PLAN.md §5.2).
/// The patterns are in <see cref="ErrorPatterns"/>. Pure and thread-safe.
/// </summary>
public sealed class ErrorClassifier
{
    /// <param name="exitCode">yt-dlp's exit code. A crash (a Windows status code) wins over the output; otherwise only the lines count.</param>
    /// <param name="lines">stdout and stderr lines, in any order; line endings may be left on.</param>
    public ErrorClassification Classify(int exitCode, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ErrorPatterns.IsCrash(exitCode))
        {
            var hex = "0x" + exitCode.ToString("X8", CultureInfo.InvariantCulture);
            args["exitCode"] = hex;
            return new(ErrorCategory.ToolBroken, new EngineMessage(ErrorCodes.Crashed, args, ErrorPatterns.CrashMessage(hex)));
        }

        var all = lines.Select(l => l.TrimEnd('\r', '\n')).ToList();
        var errors = all.Where(l => l.StartsWith("ERROR:", StringComparison.Ordinal)).ToList();
        foreach (var pattern in ErrorPatterns.Patterns)
        {
            foreach (var line in pattern.Scope == LineScope.Errors ? errors : all)
            {
                if (pattern.Matches(line, args))
                {
                    return new(pattern.Category, new EngineMessage(pattern.Code, args, pattern.Message(args)));
                }

                args.Clear();
            }
        }

        args["exitCode"] = exitCode.ToString(CultureInfo.InvariantCulture);
        return new(ErrorCategory.Unknown, new EngineMessage(ErrorCodes.Unknown, args, ErrorPatterns.UnknownMessage(exitCode)));
    }
}
