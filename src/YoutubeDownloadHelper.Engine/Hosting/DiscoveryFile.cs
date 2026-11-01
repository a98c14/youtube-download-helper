using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using YoutubeDownloadHelper.Contracts.Discovery;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// Reads and writes <c>engine.json</c> (PLAN.md §6.1). The file is written to a temporary file that is created with a
/// user-only ACL (no inherited entries) and then renamed over the old one, so readers never see a partial file and
/// the token is never readable by anyone else, not even for a moment.
/// </summary>
public static class DiscoveryFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void Write(string path, EngineDiscovery discovery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(discovery);

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileInfo(temp).Create(
                FileMode.CreateNew,
                FileSystemRights.Read | FileSystemRights.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.None,
                UserOnlySecurity()))
            {
                JsonSerializer.Serialize(stream, discovery, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            // A rename keeps the temporary file's security descriptor.
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>The discovery file, or <see langword="null"/> when there is none or it cannot be read.</summary>
    public static EngineDiscovery? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<EngineDiscovery>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the file if it still holds <paramref name="token"/>, so an engine never deletes a newer engine's file.
    /// </summary>
    public static bool DeleteIfOwned(string path, string token)
    {
        if (TryRead(path) is not { } current || !string.Equals(current.Token, token, StringComparison.Ordinal))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>Full control for the current user only; inherited entries are dropped.</summary>
    public static FileSecurity UserOnlySecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current user has no SID.");

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
