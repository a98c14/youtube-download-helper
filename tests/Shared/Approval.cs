using System.Text;

namespace YoutubeDownloadHelper.Tests.Shared;

/// <summary>
/// Approval tests against <c>&lt;folder&gt;/&lt;name&gt;.approved.&lt;extension&gt;</c> in the test project's source
/// tree. On a mismatch the actual text is written next to it as <c>&lt;name&gt;.received.&lt;extension&gt;</c>
/// (git-ignored); review it and rename it to approve. Line endings are normalized, so a CRLF checkout still matches.
/// Linked into the test projects that use it (Verify.XunitV3 fails the build with SponsorCheck SC021).
/// </summary>
internal static class Approval
{
    // The first folder above the test binaries that holds a project file: the test project's source folder.
    private static readonly Lazy<string> ProjectDir = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.csproj").Any())
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The test project folder was not found above " + AppContext.BaseDirectory);
    });

    /// <param name="actual">The text to compare.</param>
    /// <param name="folder">The snapshot folder, relative to the test project folder.</param>
    /// <param name="name">The snapshot name.</param>
    /// <param name="extension">The snapshot file extension, without the dot.</param>
    public static void Verify(string actual, string folder, string name, string extension = "txt")
    {
        actual = Normalize(actual);
        var snapshotDir = Path.Combine(ProjectDir.Value, folder);
        var approvedPath = Path.Combine(snapshotDir, $"{name}.approved.{extension}");
        var receivedPath = Path.Combine(snapshotDir, $"{name}.received.{extension}");
        var approved = File.Exists(approvedPath) ? Normalize(File.ReadAllText(approvedPath, Encoding.UTF8)) : null;

        if (string.Equals(approved, actual, StringComparison.Ordinal))
        {
            File.Delete(receivedPath);
            return;
        }

        Directory.CreateDirectory(snapshotDir);
        File.WriteAllText(receivedPath, actual, new UTF8Encoding(false));
        Assert.Fail(approved is null
            ? $"No approved snapshot. Review {receivedPath} and rename it to {Path.GetFileName(approvedPath)}."
            : $"The snapshot changed. Compare {receivedPath} with {approvedPath}.\nExpected:\n{approved}\nActual:\n{actual}");
    }

    private static string Normalize(string text) => text.ReplaceLineEndings("\n");
}
