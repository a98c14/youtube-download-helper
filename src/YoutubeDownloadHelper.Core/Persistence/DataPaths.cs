using YoutubeDownloadHelper.Contracts;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>
/// The per-user data folder and its layout (PLAN.md §8). The default root is
/// <c>%LOCALAPPDATA%\YoutubeDownloadHelperData\</c>, which must stay outside Velopack's install root; tests pass
/// their own root.
/// </summary>
public sealed class DataPaths
{
    public DataPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public static DataPaths ForCurrentUser() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            AppIdentity.DataFolderName));

    public string Root { get; }

    public string StateDb => Path.Combine(Root, "state.db");

    /// <summary>Copies of <see cref="StateDb"/> taken before migrations are applied.</summary>
    public string BackupsDir => Path.Combine(Root, "backups");

    /// <summary>The engine discovery file (user-only ACL, #44).</summary>
    public string EngineJson => Path.Combine(Root, "engine.json");

    public string AuthDir => Path.Combine(Root, "auth");

    public string CookiesBin => Path.Combine(AuthDir, "cookies.bin");

    /// <summary>The WebView2 login profile, owned by the app rather than the engine.</summary>
    public string WebViewProfileDir => Path.Combine(Root, "webview-profile");

    public string ToolsDir => Path.Combine(Root, "tools");

    public string WorkDir => Path.Combine(Root, "work");

    public string LogsDir => Path.Combine(Root, "logs");

    public string JobLogsDir => Path.Combine(LogsDir, "jobs");

    /// <summary>Partial downloads and the per-job cookies file for one item.</summary>
    public string WorkDirFor(Guid itemId) => Path.Combine(WorkDir, itemId.ToString("D"));

    /// <summary>Creates the engine-owned folders. The WebView2 profile is left to the app.</summary>
    public void EnsureCreated()
    {
        foreach (var dir in (ReadOnlySpan<string>)[Root, BackupsDir, AuthDir, ToolsDir, WorkDir, LogsDir, JobLogsDir])
        {
            Directory.CreateDirectory(dir);
        }
    }
}
