using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using YoutubeDownloadHelper.Core.Tools;

namespace YoutubeDownloadHelper.Tests.Shared;

/// <summary>
/// An in-memory release feed (no network). Each tool's "binaries" are text files; <see cref="FakeSmokeTest"/> passes
/// those whose text starts with <c>ok</c>. Linked into the test projects that use it.
/// </summary>
public sealed class FakeToolFeed : IToolReleaseFeed
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, FakeRelease> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<Uri, FakeRelease> _byUri = [];

    /// <summary>Every call fails as if there were no connection.</summary>
    public bool Offline { get; set; }

    public int Downloads { get; private set; }

    /// <summary>Publishes all three tools at <paramref name="version"/>, with working binaries.</summary>
    public FakeToolFeed PublishAll(string version = "1.0")
    {
        foreach (var tool in ManagedTool.All)
        {
            Publish(tool, version);
        }

        return this;
    }

    /// <summary>Makes a release the latest of its tool. <paramref name="content"/> goes into every binary.</summary>
    public FakeRelease Publish(ManagedTool tool, string version, string content = "ok")
    {
        var release = new FakeRelease(tool, version, content);
        lock (_lock)
        {
            _latest[tool.Name] = release;
            _byUri[release.Release.AssetUri] = release;
            _byUri[release.Release.ChecksumUri] = release;
        }

        return release;
    }

    public Task<ToolRelease> GetLatestAsync(ManagedTool tool, CancellationToken cancellationToken)
    {
        ThrowIfOffline();
        lock (_lock)
        {
            return _latest.TryGetValue(tool.Name, out var release)
                ? Task.FromResult(release.Release)
                : throw new ToolFeedException($"No {tool.Name} release.");
        }
    }

    public Task<string> GetTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        ThrowIfOffline();
        return Task.FromResult(Find(uri).ChecksumText);
    }

    public async Task DownloadAsync(Uri uri, Stream destination, Action<long>? progress, CancellationToken cancellationToken)
    {
        ThrowIfOffline();
        var release = Find(uri);
        lock (_lock)
        {
            Downloads++;
        }

        var bytes = release.Asset;
        var half = bytes.Length / 2;
        await destination.WriteAsync(bytes.AsMemory(0, half), cancellationToken);
        progress?.Invoke(half);
        if (release.Hold is { } hold)
        {
            release.HalfWay.TrySetResult();
            await hold.Task.WaitAsync(cancellationToken);
        }

        if (release.BreakHalfWay)
        {
            throw new IOException("The connection was reset.");
        }

        await destination.WriteAsync(bytes.AsMemory(half), cancellationToken);
        progress?.Invoke(bytes.Length);
    }

    private FakeRelease Find(Uri uri)
    {
        lock (_lock)
        {
            return _byUri.TryGetValue(uri, out var release) ? release : throw new HttpRequestException($"404 {uri}");
        }
    }

    private void ThrowIfOffline()
    {
        if (Offline)
        {
            throw new HttpRequestException("No connection.");
        }
    }
}

public sealed class FakeRelease
{
    public FakeRelease(ManagedTool tool, string version, string content)
    {
        Tool = tool;
        var zip = tool.Executables.Count > 1 || tool.Name == "deno";
        var assetName = zip ? $"{tool.Name}-win64.zip" : tool.MainExecutable;
        Asset = zip ? Zip(tool, version, content) : Encoding.UTF8.GetBytes($"{content} {tool.Name} {version}");
        var baseUri = new Uri($"https://feed.test/{tool.Name}/{version}/");
        Release = new ToolRelease(
            version, assetName, new Uri(baseUri, assetName), Asset.Length, new Uri(baseUri, "SHA2-256SUMS"),
            tool.Name == "yt-dlp" ? "stable" : null);
        ChecksumText = $"{Convert.ToHexStringLower(SHA256.HashData(Asset))}  {assetName}\nffff  other.zip\n";
    }

    public ManagedTool Tool { get; }

    public ToolRelease Release { get; }

    public byte[] Asset { get; }

    /// <summary>The published checksum file; correct unless a test changes it.</summary>
    public string ChecksumText { get; set; }

    /// <summary>The download fails after half the bytes.</summary>
    public bool BreakHalfWay { get; set; }

    /// <summary>When set, the download waits for it after half the bytes (and sets <see cref="HalfWay"/>).</summary>
    public TaskCompletionSource? Hold { get; set; }

    public TaskCompletionSource HalfWay { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A checksum that is valid hex but for other bytes.</summary>
    public void CorruptChecksum() =>
        ChecksumText = $"{Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]))}  {Release.AssetName}\n";

    private static byte[] Zip(ManagedTool tool, string version, string content)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var exe in tool.Executables)
            {
                using var writer = new StreamWriter(zip.CreateEntry($"{tool.Name}-{version}/bin/{exe}").Open());
                writer.Write($"{content} {exe} {version}");
            }

            using var readme = new StreamWriter(zip.CreateEntry($"{tool.Name}-{version}/README.txt").Open());
            readme.Write("not a binary");
        }

        return memory.ToArray();
    }
}

/// <summary>Passes a "binary" whose text starts with <c>ok</c>; counts its runs.</summary>
public sealed class FakeSmokeTest : IToolSmokeTest
{
    private int _runs;

    public int Runs => Volatile.Read(ref _runs);

    public async Task<ToolSmokeResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _runs);
        if (!File.Exists(executable))
        {
            return new ToolSmokeResult(false, "missing");
        }

        var text = await File.ReadAllTextAsync(executable, cancellationToken);
        return text.StartsWith("ok", StringComparison.Ordinal) ? new ToolSmokeResult(true, text) : new ToolSmokeResult(false, text);
    }
}
