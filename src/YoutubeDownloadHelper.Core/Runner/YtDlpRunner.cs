using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>
/// Runs yt-dlp commands from <see cref="YtDlpCommandBuilder"/> (PLAN.md §5.4). Each run is a
/// <see cref="JobObjectProcess"/> with no <c>PYTHON*</c> variables; cancelling kills the whole process tree.
/// Both streams are read as UTF-8 (invalid bytes replaced) and split on <c>\n</c>. <c>YDH1</c> lines feed a
/// <see cref="ProgressTracker"/>; the rest is raw output for the output tail and the error classifier. Thread-safe.
/// </summary>
public sealed partial class YtDlpRunner(TimeProvider time, ILogger<YtDlpRunner> logger)
{
    /// <summary>How many lines <see cref="YtDlpRunResult.OutputTail"/> and <see cref="YtDlpRunResult.ErrorLines"/> keep.</summary>
    public const int TailLines = 200;

    // After yt-dlp exits, its tree is killed, which closes the pipes; this only guards against a stray handle.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Runs a download (or any command with line output) to the end, or until <paramref name="cancellationToken"/> fires.</summary>
    /// <returns>The result; a cancel returns a result with <see cref="YtDlpRunResult.Cancelled"/> instead of throwing.</returns>
    /// <exception cref="OperationCanceledException">Cancelled before the process started.</exception>
    /// <exception cref="Win32Exception">The process could not be started (e.g. yt-dlp is missing).</exception>
    public async Task<YtDlpRunResult> RunAsync(
        YtDlpCommand command, YtDlpRunOptions? options = null, CancellationToken cancellationToken = default) =>
        (await RunCoreAsync(command, options ?? new(), json: false, cancellationToken).ConfigureAwait(false)).Run;

    /// <summary>Runs a command that prints one JSON document on stdout (<c>-J</c>).</summary>
    /// <inheritdoc cref="RunAsync" path="/exception"/>
    public Task<YtDlpJsonResult> RunJsonAsync(
        YtDlpCommand command, YtDlpRunOptions? options = null, CancellationToken cancellationToken = default) =>
        RunCoreAsync(command, options ?? new(), json: true, cancellationToken);

    /// <summary>Runs a resolve (<see cref="YtDlpCommandBuilder.Resolve(DownloadRequest)"/>) and maps its JSON.</summary>
    /// <inheritdoc cref="RunAsync" path="/exception"/>
    public async Task<YtDlpResolveResult> ResolveAsync(
        YtDlpCommand command, YtDlpRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await RunJsonAsync(command, options, cancellationToken).ConfigureAwait(false);
        return new YtDlpResolveResult(result.Run, result.Run.Succeeded ? ResolvedVideo.Parse(result.Json) : null);
    }

    private async Task<YtDlpJsonResult> RunCoreAsync(
        YtDlpCommand command, YtDlpRunOptions options, bool json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        using var output = new RunOutput(options, time, logger);
        output.Header(command);
        var environment = WindowsCommandLine.ChildEnvironment(Environment.GetEnvironmentVariables());
        using var process = JobObjectProcess.Start(command.FileName, command.Arguments, environment);
        LogStarted(logger, process.Id, Path.GetFileName(command.FileName));

        var stdout = json ? ReadAllAsync(process.StandardOutput) : ReadLinesAsync(process.StandardOutput, output.Line);
        var stderr = ReadLinesAsync(process.StandardError, output.Line);

        var exit = process.WaitForExitAsync(CancellationToken.None);
        var killed = false;
        int exitCode;
        using (cancellationToken.Register(() =>
               {
                   if (!exit.IsCompleted)
                   {
                       Volatile.Write(ref killed, true);
                       process.Kill();
                   }
               }))
        {
            exitCode = await exit.ConfigureAwait(false);
        }

        // Whatever the tree still runs (a detached child) goes too, so the pipes close.
        process.Kill();
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainTimeout, time, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogDrainTimeout(logger, process.Id);
        }

        var cancelled = Volatile.Read(ref killed);
        var text = stdout.IsCompletedSuccessfully ? stdout.Result : "";
        if (json)
        {
            output.Note(string.Create(CultureInfo.InvariantCulture, $"stdout: {text.Length} characters of JSON"));
        }

        output.Note(string.Create(CultureInfo.InvariantCulture, $"exit code {exitCode}{(cancelled ? " (cancelled)" : "")}"));
        LogExited(logger, process.Id, exitCode, cancelled);
        return new YtDlpJsonResult(output.Result(exitCode, cancelled), text);
    }

    private static Task<string> ReadLinesAsync(Stream stream, Action<string> onLine) =>
        Task.Factory.StartNew(
            () =>
            {
                using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false);
                var buffer = new char[4096];
                var line = new StringBuilder();
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        if (buffer[i] == '\n')
                        {
                            onLine(EndLine(line));
                        }
                        else
                        {
                            line.Append(buffer[i]);
                        }
                    }
                }

                if (line.Length > 0)
                {
                    onLine(EndLine(line));
                }

                return "";
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static Task<string> ReadAllAsync(Stream stream) =>
        Task.Factory.StartNew(
            () =>
            {
                using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false);
                return reader.ReadToEnd();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>yt-dlp ends lines with <c>\n</c> on Windows too; a trailing <c>\r</c> is dropped (spike #33).</summary>
    private static string EndLine(StringBuilder line)
    {
        var length = line.Length > 0 && line[^1] == '\r' ? line.Length - 1 : line.Length;
        var text = line.ToString(0, length);
        line.Clear();
        return text;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Started {Tool} (pid {ProcessId})")]
    private static partial void LogStarted(ILogger logger, int processId, string tool);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Process {ProcessId} exited with {ExitCode} (cancelled: {Cancelled})")]
    private static partial void LogExited(ILogger logger, int processId, int exitCode, bool cancelled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The output of process {ProcessId} did not end after it exited")]
    private static partial void LogDrainTimeout(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the job log {Path}")]
    private static partial void LogJobLogFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A progress handler failed")]
    private static partial void LogProgressFailed(ILogger logger, Exception exception);

    /// <summary>What one run collects from both streams: the tracker, the tail, the error lines and the job log.</summary>
    private sealed class RunOutput : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly TimeProvider _time;
        private readonly ILogger _logger;
        private readonly string? _logPath;
        private readonly ProgressTracker _tracker = new();
        private readonly Queue<string> _tail = new();
        private readonly Queue<string> _errors = new();
        private readonly ProgressThrottle<DownloadProgress>? _throttle;
        private StreamWriter? _log;

        public RunOutput(YtDlpRunOptions options, TimeProvider time, ILogger logger)
        {
            _time = time;
            _logger = logger;
            _logPath = options.LogFile;
            if (options.Progress is { } progress)
            {
                _throttle = new ProgressThrottle<DownloadProgress>(
                    p =>
                    {
                        try
                        {
                            progress(p);
                        }
                        catch (Exception ex)
                        {
                            LogProgressFailed(logger, ex);
                        }
                    },
                    time);
            }

            if (_logPath is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                    _log = new StreamWriter(_logPath, append: true, Utf8) { AutoFlush = true, NewLine = "\n" };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogJobLogFailed(logger, _logPath, ex);
                }
            }
        }

        public void Header(YtDlpCommand command)
        {
            var started = _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            Note($"{started} {WindowsCommandLine.Build(command.FileName, command.Arguments)}");
        }

        /// <summary>A line of the runner's own in the job log, marked with <c>#</c>.</summary>
        public void Note(string text)
        {
            lock (_lock)
            {
                Write("# " + text);
            }
        }

        public void Line(string line)
        {
            lock (_lock)
            {
                Write(line);
                var kind = _tracker.Read(line);
                if (kind != OutputLineKind.Download)
                {
                    Keep(_tail, line);
                }

                if (kind == OutputLineKind.Raw)
                {
                    if (line.StartsWith("ERROR:", StringComparison.Ordinal) || line.StartsWith("WARNING:", StringComparison.Ordinal))
                    {
                        Keep(_errors, line);
                    }
                }
                else if (kind != OutputLineKind.Plan)
                {
                    _throttle?.Post(_tracker.Snapshot());
                }
            }
        }

        public YtDlpRunResult Result(int exitCode, bool cancelled)
        {
            lock (_lock)
            {
                _throttle?.Dispose();
                return new YtDlpRunResult(
                    exitCode,
                    cancelled,
                    _tracker.FinalPath,
                    _tracker.AlreadyPresent,
                    [.. _errors],
                    [.. _tail],
                    _tracker.Snapshot());
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _throttle?.Dispose();
                _log?.Dispose();
                _log = null;
            }
        }

        private static void Keep(Queue<string> lines, string line)
        {
            if (lines.Count == TailLines)
            {
                lines.Dequeue();
            }

            lines.Enqueue(line);
        }

        private void Write(string line)
        {
            if (_log is null)
            {
                return;
            }

            try
            {
                _log.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogJobLogFailed(_logger, _logPath!, ex);
                _log.Dispose();
                _log = null;
            }
        }
    }
}
