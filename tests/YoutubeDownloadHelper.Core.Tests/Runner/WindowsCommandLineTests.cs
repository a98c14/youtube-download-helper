using System.Collections;
using System.Runtime.InteropServices;
using YoutubeDownloadHelper.Core.Runner;

namespace YoutubeDownloadHelper.Core.Tests.Runner;

public sealed class WindowsCommandLineTests
{
    public static TheoryData<string[]> ArgumentLists => new(
        ["plain", "two words", "", "tab\there"],
        ["\"quoted\"", "a\"b", "\\\"", "trailing\\", "trailing space\\", "\\\\server\\share\\", "x\\\\\"y"],
        [@"C:\Users\Kullanıcı İ 😀\Downloads\Youtube İndirgeç", "temp:C:\\work dir\\", "-o", @"%(channel)s\%(title)s.%(ext)s"],
        ["download:YDH1 {\"k\":\"dl\",\"p\":%(progress.{status,downloaded_bytes})j}", "--", "https://fake.test/success?a=1&b=2"]);

    [Theory]
    [MemberData(nameof(ArgumentLists))]
    public void Every_argument_reaches_the_child_unchanged(string[] arguments)
    {
        var commandLine = WindowsCommandLine.Build(@"C:\Program Files\yt-dlp İ\yt-dlp.exe", arguments);

        Assert.Equal([@"C:\Program Files\yt-dlp İ\yt-dlp.exe", .. arguments], Parse(commandLine));
    }

    [Fact]
    public void Python_variables_are_removed_from_the_child_environment()
    {
        var variables = new Hashtable
        {
            ["PATH"] = @"C:\Windows",
            ["PYTHONUTF8"] = "1",
            ["PYTHONIOENCODING"] = "utf-8:surrogateescape",
            ["pythonpath"] = @"C:\py",
            ["MY_PYTHON"] = "kept",
            ["İ_VAR"] = "ı",
        };

        var environment = WindowsCommandLine.ChildEnvironment(variables);

        Assert.Equal(["MY_PYTHON", "PATH", "İ_VAR"], environment.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("ı", environment["İ_VAR"]);
    }

    [Fact]
    public void The_environment_block_ends_with_two_nulls()
    {
        Assert.Equal("A=1\0b=2\0\0", WindowsCommandLine.EnvironmentBlock([new("A", "1"), new("b", "2")]));
        Assert.Equal("\0\0", WindowsCommandLine.EnvironmentBlock([]));
    }

    private static string[] Parse(string commandLine)
    {
        var argv = CommandLineToArgv(commandLine, out var count);
        try
        {
            return [.. Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CommandLineToArgvW")]
    private static extern IntPtr CommandLineToArgv(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
