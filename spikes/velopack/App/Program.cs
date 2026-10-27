// Fake App.exe: Velopack hooks + update flow, engine supervision, toasts. Built in code (no XAML).
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Toolkit.Uwp.Notifications;
using Velopack;

namespace VelopackSpike.App;

public static class Program
{
    public const string DisplayName = "Youtube İndirgeç";
    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YdhVelopackSpike", "app.log");

    public static string Version { get; } =
        Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    public static void Log(string msg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [app {Version} pid {Environment.ProcessId}] {msg}{Environment.NewLine}");
    }

    [STAThread]
    public static void Main(string[] args)
    {
        Log($"Main args=[{string.Join(" ", args)}] path={Environment.ProcessPath}");
        // Must run first: handles the --veloapp-* hook invocations and exits for them.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(v => Log($"hook after-install {v}"))
            .OnAfterUpdateFastCallback(v => Log($"hook after-update {v}"))
            .OnBeforeUpdateFastCallback(v => Log($"hook before-update {v}"))
            .OnBeforeUninstallFastCallback(v => Log($"hook before-uninstall {v}"))
            .OnFirstRun(v => Log($"first run {v}"))
            .OnRestarted(v => Log($"restarted after update {v}"))
            .Run();

        ToastNotificationManagerCompat.OnActivated += e => Log($"toast activated args='{e.Argument}'");

        var app = new Application();
        app.Run(new MainWindow());
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr appId);

    public static string ProcessAumid()
    {
        if (GetCurrentProcessExplicitAppUserModelID(out var p) != 0) return "(none)";
        var s = Marshal.PtrToStringUni(p)!;
        Marshal.FreeCoTaskMem(p);
        return s;
    }
}

sealed class MainWindow : Window
{
    const string EngineUrl = "http://127.0.0.1:47123/";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    readonly TextBox _out = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 360 };
    readonly UpdateManager _mgr;
    UpdateInfo? _update;

    public MainWindow()
    {
        Title = $"{Program.DisplayName} {Program.Version}";
        Width = 760; Height = 520;
        var feed = Assembly.GetEntryAssembly()!.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "FeedPath").Value!;
        _mgr = new UpdateManager(feed);

        var buttons = new WrapPanel();
        void Add(string text, Func<Task> action)
        {
            var b = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
            b.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Print($"ERROR {ex}"); } };
            buttons.Children.Add(b);
        }

        Add("Engine info", EngineInfo);
        Add("Start engine", () => { StartEngine(); return Task.CompletedTask; });
        Add("Check + download update", CheckAndDownload);
        Add("Apply (graceful engine stop)", () => Apply(stopEngine: true));
        Add("Apply (engine left running)", () => Apply(stopEngine: false));
        Add("Toast", () => { Toast(); return Task.CompletedTask; });

        var root = new StackPanel { Margin = new Thickness(8) };
        root.Children.Add(buttons);
        root.Children.Add(_out);
        Content = root;

        Print($"app {Program.Version} installed={_mgr.IsInstalled} current={_mgr.CurrentVersion} appId={_mgr.AppId}");
        Print($"exe {Environment.ProcessPath}");
        Print($"process AUMID {Program.ProcessAumid()}");
        Print($"feed {feed}");
        Loaded += async (_, _) =>
        {
            // Supervisor: start the engine if it does not answer.
            if (!await EngineAlive()) StartEngine();
            await Task.Delay(1500);
            await EngineInfo();
        };
    }

    void Print(string s)
    {
        Program.Log(s);
        _out.AppendText(s + Environment.NewLine);
        _out.ScrollToEnd();
    }

    static async Task<bool> EngineAlive()
    {
        try { await Http.GetStringAsync(EngineUrl + "info"); return true; } catch { return false; }
    }

    async Task EngineInfo()
    {
        try { Print("engine " + await Http.GetStringAsync(EngineUrl + "info")); }
        catch (Exception ex) { Print("engine not reachable: " + ex.Message); }
    }

    void StartEngine()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Engine.exe");
        var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true });
        Print($"started engine pid {p?.Id} from {exe}");
    }

    async Task CheckAndDownload()
    {
        _update = await _mgr.CheckForUpdatesAsync();
        if (_update is null) { Print("no update"); return; }
        Print($"update available {_update.TargetFullRelease.Version}, downloading");
        await _mgr.DownloadUpdatesAsync(_update, p => Dispatcher.Invoke(() => Title = $"{Program.DisplayName} {Program.Version} — {p}%"));
        Print("downloaded");
    }

    async Task Apply(bool stopEngine)
    {
        if (_update is null) { Print("check first"); return; }
        if (stopEngine)
        {
            var engine = Process.GetProcessesByName("Engine").FirstOrDefault();
            await Http.PostAsync(EngineUrl + "shutdown", null);
            Print($"shutdown sent, waiting for engine pid {engine?.Id}");
            if (engine is not null && !engine.WaitForExit(TimeSpan.FromSeconds(15)))
                Print("engine did not exit in 15 s");
            Print("engine stopped");
        }
        Print($"applying {_update.TargetFullRelease.Version}");
        _mgr.ApplyUpdatesAndRestart(_update);
    }

    void Toast()
    {
        new ToastContentBuilder()
            .AddArgument("action", "openFolder")
            .AddText("İndirme tamamlandı")
            .AddText("Çalışma ağacı — ğüşöçıİ")
            .Show();
        Print("toast shown");
    }
}
