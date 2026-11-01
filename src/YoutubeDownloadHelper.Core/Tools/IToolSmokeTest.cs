using System.ComponentModel;
using System.Text;
using YoutubeDownloadHelper.Core.Runner;

namespace YoutubeDownloadHelper.Core.Tools;

/// <summary>
/// Runs a tool binary to see that it works (PLAN.md §5.6). <see cref="ProcessToolSmokeTest"/> is the default; the tool
/// manager's tests use a fake, since their downloaded "binaries" are not real programs.
/// </summary>
public interface IToolSmokeTest
{
    /// <summary>Runs <paramref name="executable"/> with <paramref name="arguments"/>. Never throws for a broken binary.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    Task<ToolSmokeResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <param name="Passed">The binary started and exited 0 in time.</param>
/// <param name="Output">Its first line of output (the version), or why it failed.</param>
public sealed record ToolSmokeResult(bool Passed, string Output);

/// <summary>
/// Starts the binary in a <see cref="JobObjectProcess"/> (no <c>PYTHON*</c> variables, as for real runs); passes when it
/// exits 0 within <see cref="Timeout"/>.
/// </summary>
public sealed class ProcessToolSmokeTest(TimeProvider time) : IToolSmokeTest
{
    /// <summary>yt-dlp's one-file build unpacks itself first, which is slow with a cold antivirus cache.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public async Task<ToolSmokeResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executable))
        {
            return new ToolSmokeResult(false, "The file is missing.");
        }

        JobObjectProcess process;
        try
        {
            process = JobObjectProcess.Start(
                executable, arguments, WindowsCommandLine.ChildEnvironment(Environment.GetEnvironmentVariables()));
        }
        catch (Win32Exception ex)
        {
            return new ToolSmokeResult(false, ex.Message);
        }

        using (process)
        {
            var stdout = ReadAsync(process.StandardOutput);
            var stderr = ReadAsync(process.StandardError);
            using var timeout = new CancellationTokenSource(Timeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            int exitCode;
            try
            {
                exitCode = await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                cancellationToken.ThrowIfCancellationRequested();
                return new ToolSmokeResult(false, "It did not exit in time.");
            }

            // Closes the pipes if it left a child behind.
            process.Kill();
            var output = await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            var first = FirstLine(output) ?? FirstLine(errors) ?? "";
            return exitCode == 0
                ? new ToolSmokeResult(true, first)
                : new ToolSmokeResult(false, $"It exited with code {exitCode}: {first}");
        }
    }

    private static Task<string> ReadAsync(Stream stream) => Task.Run(async () =>
    {
        using var reader = new StreamReader(stream, Utf8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    });

    private static string? FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim() is { Length: > 0 } trimmed)
            {
                return trimmed;
            }
        }

        return null;
    }
}
