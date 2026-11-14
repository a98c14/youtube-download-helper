using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>Files that hold secrets in plain text (the discovery file, a job's cookies file): readable only by the user.</summary>
public static class UserOnlyFile
{
    /// <summary>Full control for the current user only; inherited entries are dropped.</summary>
    public static FileSecurity Security()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current user has no SID.");

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Writes <paramref name="contents"/> (UTF-8, no BOM) to a new file that is created with <see cref="Security"/>, so
    /// it is never readable by anyone else, not even for a moment. An existing file is deleted first: overwriting it
    /// would keep its ACL.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        File.Delete(path);
        using var stream = new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.Read | FileSystemRights.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None,
            Security());
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
        stream.Write(bytes);
    }
}
