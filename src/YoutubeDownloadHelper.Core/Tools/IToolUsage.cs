namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Which tool versions running yt-dlp processes use, so the tool manager never garbage-collects a version a job still
/// runs (PLAN.md §5.6). <see cref="Runner.YtDlpRunner"/> holds a lease for each process. <see cref="NoToolUsage"/> is
/// the default; the <see cref="ToolManager"/> replaces it.
/// </summary>
public interface IToolUsage
{
    /// <summary>Marks the versions behind <paramref name="tools"/> in use until the lease is disposed.</summary>
    IDisposable Use(ToolPaths tools);
}

/// <summary>Tracks nothing: nothing deletes tool versions.</summary>
public sealed class NoToolUsage : IToolUsage
{
    public IDisposable Use(ToolPaths tools) => Lease.Instance;

    private sealed class Lease : IDisposable
    {
        public static Lease Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
