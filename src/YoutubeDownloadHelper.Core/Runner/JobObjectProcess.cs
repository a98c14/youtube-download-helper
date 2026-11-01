using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static YoutubeDownloadHelper.Core.Runner.NativeMethods;

namespace YoutubeDownloadHelper.Core.Runner;

/// <summary>
/// A child process inside its own Windows Job Object with <c>KILL_ON_JOB_CLOSE</c> (PLAN.md §5.4). The process is
/// created suspended and joins the job before its first instruction, so every process it starts (ffmpeg, Deno, the
/// PyInstaller child of <c>yt-dlp.exe</c>) is in the job too. <see cref="Kill"/> ends the whole tree, and so does
/// disposing, or the engine exiting for any reason.
/// </summary>
/// <remarks>
/// Only the child's three standard handles are inherited (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>). stdin is an
/// empty pipe; stdout and stderr are raw byte streams.
/// </remarks>
public sealed class JobObjectProcess : IDisposable
{
    private readonly SafeJobHandle _job;
    private readonly SafeProcessHandle _process;
    private readonly Task<int> _exit;

    // The process handle has two owners, Dispose and the exit wait; the second to let go closes it.
    private int _processOwners = 2;

    private JobObjectProcess(SafeJobHandle job, SafeProcessHandle process, int id, Stream stdout, Stream stderr)
    {
        _job = job;
        _process = process;
        Id = id;
        StandardOutput = stdout;
        StandardError = stderr;
        _exit = WaitForExitCoreAsync();
    }

    public int Id { get; }

    public Stream StandardOutput { get; }

    public Stream StandardError { get; }

    /// <summary>Starts <paramref name="fileName"/> with <paramref name="arguments"/>.</summary>
    /// <param name="fileName">The executable's full path.</param>
    /// <param name="arguments">One argv entry each; quoted by <see cref="WindowsCommandLine.Build"/>.</param>
    /// <param name="workingDirectory">The child's working directory; <see langword="null"/> for the engine's.</param>
    /// <param name="environment">The child's whole environment; <see langword="null"/> inherits the engine's.</param>
    public static JobObjectProcess Start(
        string fileName,
        IEnumerable<string> arguments,
        IEnumerable<KeyValuePair<string, string>>? environment = null,
        string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var job = CreateJob();
        SafeFileHandle? stdinRead = null, stdinWrite = null, stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null;
        var commandLine = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var handleList = IntPtr.Zero;
        try
        {
            (stdinRead, stdinWrite) = Pipe(parentReads: false);
            (stdoutRead, stdoutWrite) = Pipe(parentReads: true);
            (stderrRead, stderrWrite) = Pipe(parentReads: true);

            commandLine = Marshal.StringToHGlobalUni(WindowsCommandLine.Build(fileName, arguments));
            if (environment is not null)
            {
                environmentBlock = Marshal.StringToHGlobalUni(WindowsCommandLine.EnvironmentBlock(environment));
            }

            // Inherit only the child's ends of the pipes, never other inheritable handles the engine has open.
            IntPtr[] inherited = [stdinRead.DangerousGetHandle(), stdoutWrite.DangerousGetHandle(), stderrWrite.DangerousGetHandle()];
            handleList = Marshal.AllocHGlobal(IntPtr.Size * inherited.Length);
            Marshal.Copy(inherited, 0, handleList, inherited.Length);
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
            {
                Marshal.FreeHGlobal(attributeList);
                attributeList = IntPtr.Zero;
                throw LastError("InitializeProcThreadAttributeList");
            }

            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, handleList,
                    IntPtr.Size * inherited.Length, IntPtr.Zero, IntPtr.Zero))
            {
                throw LastError("UpdateProcThreadAttribute");
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StdInput = inherited[0],
                    StdOutput = inherited[1],
                    StdError = inherited[2],
                },
                AttributeList = attributeList,
            };
            var flags = CreateSuspended | CreateNoWindow | ExtendedStartupInfoPresent |
                (environment is null ? 0 : CreateUnicodeEnvironment);
            if (!CreateProcess(fileName, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles: true, flags,
                    environmentBlock, workingDirectory, ref startup, out var info))
            {
                throw LastError($"Starting {Path.GetFileName(fileName)}");
            }

            var process = new SafeProcessHandle(info.Process, ownsHandle: true);
            try
            {
                if (!AssignProcessToJobObject(job, process))
                {
                    var error = LastError("AssignProcessToJobObject");
                    TerminateProcess(process, 1);
                    throw error;
                }

                if (ResumeThread(info.Thread) == WaitFailed)
                {
                    var error = LastError("ResumeThread");
                    TerminateJobObject(job, 1);
                    throw error;
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }
            finally
            {
                CloseHandle(info.Thread);
            }

            var stdout = new FileStream(stdoutRead, FileAccess.Read, bufferSize: 0, isAsync: false);
            var stderr = new FileStream(stderrRead, FileAccess.Read, bufferSize: 0, isAsync: false);
            stdoutRead = stderrRead = null;
            var result = new JobObjectProcess(job, process, info.ProcessId, stdout, stderr);
            job = null!;
            return result;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            Marshal.FreeHGlobal(handleList);
            Marshal.FreeHGlobal(commandLine);
            Marshal.FreeHGlobal(environmentBlock);

            // The child holds its own copies; closing ours lets the reads end when the child (tree) exits, and
            // closing the stdin write end gives the child an empty stdin.
            stdinRead?.Dispose();
            stdinWrite?.Dispose();
            stdoutWrite?.Dispose();
            stderrWrite?.Dispose();
            stdoutRead?.Dispose();
            stderrRead?.Dispose();
            job?.Dispose();
        }
    }

    /// <summary>Waits for the started process (not the rest of the tree) to exit and returns its exit code.</summary>
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exit.WaitAsync(cancellationToken);

    /// <summary>Kills every process in the job: the started process and everything it started. Idempotent.</summary>
    public void Kill()
    {
        if (!_job.IsClosed)
        {
            TerminateJobObject(_job, 1);
        }
    }

    /// <summary>Closes the job (killing what is still running in it) and the process handle.</summary>
    public void Dispose()
    {
        if (_job.IsClosed)
        {
            return;
        }

        _job.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        ReleaseProcess();
    }

    private void ReleaseProcess()
    {
        if (Interlocked.Decrement(ref _processOwners) == 0)
        {
            _process.Dispose();
        }
    }

    private Task<int> WaitForExitCoreAsync()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitHandle = new ProcessWaitHandle(_process);
        RegisteredWaitHandle? registration = null;
        registration = ThreadPool.RegisterWaitForSingleObject(waitHandle, (_, _) =>
        {
            registration?.Unregister(null);
            waitHandle.Dispose();
            if (GetExitCodeProcess(_process, out var code))
            {
                completion.TrySetResult(unchecked((int)code));
            }
            else
            {
                completion.TrySetException(LastError("GetExitCodeProcess"));
            }

            ReleaseProcess();
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        return completion.Task;
    }

    private static SafeJobHandle CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw LastError("CreateJobObject");
        }

        var limits = new JobObjectExtendedLimitInformationData
        {
            // DIE_ON_UNHANDLED_EXCEPTION: a crashing ffmpeg ends instead of waiting on an error dialog nobody sees.
            BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitDieOnUnhandledException },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf(limits)))
        {
            var error = LastError("SetInformationJobObject");
            job.Dispose();
            throw error;
        }

        return job;
    }

    /// <summary>An anonymous pipe; only the child's end is inheritable.</summary>
    private static (SafeFileHandle Read, SafeFileHandle Write) Pipe(bool parentReads)
    {
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        if (!CreatePipe(out var read, out var write, ref attributes, 0))
        {
            throw LastError("CreatePipe");
        }

        if (!SetHandleInformation(parentReads ? read : write, HandleFlagInherit, 0))
        {
            var error = LastError("SetHandleInformation");
            read.Dispose();
            write.Dispose();
            throw error;
        }

        return (read, write);
    }

    /// <summary>Waits on the process handle without owning it.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
