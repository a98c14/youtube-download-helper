using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// One engine per Windows user (PLAN.md §4.1), held as a named mutex for the life of the process. The name holds the
/// user's SID and a hash of the data folder, so it spans the user's sessions, and an engine run against another data
/// folder (development, tests) does not count. The mutex is only ever created, never waited on, so its thread
/// affinity does not matter.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static string MutexName(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");

        // Windows paths are case-insensitive.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(paths.Root).ToUpperInvariant()));
        return $@"Global\{Contracts.AppIdentity.InternalName}.Engine.{sid}.{Convert.ToHexString(hash, 0, 8)}";
    }

    /// <summary>The instance, or <see langword="null"/> when another process holds <paramref name="name"/>.</summary>
    public static SingleInstance? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            // CurrentUserOnly: the mutex is created with a user-only ACL, and an existing one is only opened if the
            // current user owns it.
            var mutex = new Mutex(
                initiallyOwned: false,
                name,
                new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false },
                out var createdNew);
            if (createdNew)
            {
                return new SingleInstance(mutex);
            }

            mutex.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Someone else's object with this name; treat it as taken.
            return null;
        }
    }

    public void Dispose() => _mutex.Dispose();
}
