namespace YoutubeDownloadHelper.Core.Scheduling;

/// <summary>Free disk space, for the <c>DiskFull</c> queue block (PLAN.md §4.3). <see cref="DriveDiskSpace"/> is the default.</summary>
public interface IDiskSpace
{
    /// <summary>
    /// The bytes available to the user on the drive that holds <paramref name="path"/> (which need not exist), or
    /// <see langword="null"/> when that is unknown (e.g. a network share); unknown counts as enough.
    /// </summary>
    long? GetAvailableBytes(string path);
}

/// <summary><see cref="DriveInfo.AvailableFreeSpace"/> of the path's root.</summary>
public sealed class DriveDiskSpace : IDiskSpace
{
    public long? GetAvailableBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
