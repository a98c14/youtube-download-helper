using System.Text;

namespace YoutubeDownloadHelper.Core.Tests.Runner;

/// <summary>
/// Approval tests against <c>Runner/Snapshots/&lt;name&gt;.approved.txt</c> in the source tree. On a mismatch the
/// actual text is written next to it as <c>&lt;name&gt;.received.txt</c> (git-ignored); review it and rename it to
/// approve. Line endings are normalized, so a CRLF checkout still matches.
/// </summary>
internal static class Approval
{
    private static readonly Lazy<string> SnapshotDir = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "YoutubeDownloadHelper.Core.Tests.csproj")))
            {
                return Path.Combine(dir.FullName, "Runner", "Snapshots");
            }
        }

        throw new InvalidOperationException("The test project folder was not found above " + AppContext.BaseDirectory);
    });

    public static void Verify(string actual, string name)
    {
        actual = Normalize(actual);
        var approvedPath = Path.Combine(SnapshotDir.Value, name + ".approved.txt");
        var receivedPath = Path.Combine(SnapshotDir.Value, name + ".received.txt");
        var approved = File.Exists(approvedPath) ? Normalize(File.ReadAllText(approvedPath, Encoding.UTF8)) : null;

        if (string.Equals(approved, actual, StringComparison.Ordinal))
        {
            File.Delete(receivedPath);
            return;
        }

        Directory.CreateDirectory(SnapshotDir.Value);
        File.WriteAllText(receivedPath, actual, new UTF8Encoding(false));
        Assert.Fail(approved is null
            ? $"No approved snapshot. Review {receivedPath} and rename it to {Path.GetFileName(approvedPath)}."
            : $"The snapshot changed. Compare {receivedPath} with {approvedPath}.\nExpected:\n{approved}\nActual:\n{actual}");
    }

    private static string Normalize(string text) => text.ReplaceLineEndings("\n");
}
