using System.Diagnostics;
using System.Text;

namespace YoutubeDownloadHelper.Core.Tests.FakeYtDlp;

internal sealed record FakeYtDlpResult(int ExitCode, IReadOnlyList<string> Stdout, IReadOnlyList<string> Stderr)
{
    public IEnumerable<string> Lines(string kind) =>
        Stdout.Concat(Stderr).Where(l => l.StartsWith($"YDH1 {{\"k\":\"{kind}\"", StringComparison.Ordinal));
}

/// <summary>Starts <c>YoutubeDownloadHelper.FakeYtDlp.exe</c> (copied next to the tests by the project reference).</summary>
internal static class FakeYtDlpProcess
{
    public static string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "YoutubeDownloadHelper.FakeYtDlp.exe");

    /// <summary>The engine's fixed arguments from spike #33 (PLAN.md §5.4), minus the tool paths.</summary>
    public static string[] EngineArgs(string temp, string home) =>
    [
        "--ignore-config", "--no-playlist", "--newline", "--progress", "--progress-delta", "0.25",
        "-P", "temp:" + temp, "-P", "home:" + home,
        "-o", "%(channel)s\\%(upload_date>%Y-%m-%d)s - %(title)s.%(ext)s",
        "--progress-template",
        "download:YDH1 {\"k\":\"dl\",\"p\":%(progress.{status,downloaded_bytes,total_bytes,total_bytes_estimate,speed,eta,elapsed,fragment_index,fragment_count})j,\"i\":%(info.{id,format_id})j}",
        "--progress-template",
        "postprocess:YDH1 {\"k\":\"pp\",\"p\":%(progress.{status,postprocessor})j,\"i\":%(info.{id,filepath})j}",
        "--print",
        "before_dl:YDH1 {\"k\":\"plan\",\"i\":%(.{id,title,channel,format_id,ext,filesize,filesize_approx})j,\"rf\":%(requested_formats.:.{format_id,ext,protocol,filesize,filesize_approx})j}",
        "--print", "after_move:YDH1 {\"k\":\"moved\",\"i\":%(.{id,filepath})j}",
        "--embed-metadata", "--embed-thumbnail", "--embed-chapters", "--encoding", "utf-8",
    ];

    public static Process Start(IEnumerable<string> args, Encoding? encoding = null)
    {
        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? new UTF8Encoding(false),
            StandardErrorEncoding = encoding ?? new UTF8Encoding(false),
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start)!;
    }

    public static async Task<FakeYtDlpResult> RunAsync(IEnumerable<string> args, Encoding? encoding = null)
    {
        using var process = Start(args, encoding);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new FakeYtDlpResult(process.ExitCode, SplitLines(await stdout), SplitLines(await stderr));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    public static Task<FakeYtDlpResult> RunAsync(params string[] args) => RunAsync(args, encoding: null);

    /// <summary>Reads stdout until a line matches, for scenarios that never exit.</summary>
    public static async Task<string> ReadUntilAsync(Process process, Func<string, bool> match)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            if (match(line))
            {
                return line;
            }
        }

        throw new InvalidOperationException("The fake closed stdout before the expected line.");
    }

    /// <summary>yt-dlp ends lines with <c>\n</c> only, so a stray <c>\r</c> stays visible to the assertions.</summary>
    private static string[] SplitLines(string text) =>
        text.Length == 0 ? [] : text.TrimEnd('\n').Split('\n');
}

/// <summary>A temporary folder with Turkish and emoji characters in its name (spike #33).</summary>
internal sealed class FakeYtDlpFolder : IDisposable
{
    public string Root { get; } = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "ydh-fake-ytdlp", "Kullanıcı İ 😀 " + Guid.NewGuid().ToString("N")[..8])).FullName;

    public string Temp => Path.Combine(Root, "work");

    public string Home => Path.Combine(Root, "home");

    public string In(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
