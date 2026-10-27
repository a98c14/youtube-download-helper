// Throwaway spike for issue #31: Google sign-in inside WebView2 and Netscape cookies.txt export.
// See README.md in this folder. Cookie VALUES are written only to the cookies file, never to logs.

using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WebView2Login;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Options.PrintUsage();
            return 2;
        }

        Log.Open(options.LogDir);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new LoginWindow(options);
        app.Run(window);
        Log.Write(window.ExitCode == 0 ? "Done." : "Closed without exporting cookies.");
        return window.ExitCode;
    }
}

internal sealed record Options(
    string ProfileDir,
    string OutFile,
    string LogDir,
    string StartUrl,
    string? UserAgent,
    string? BrowserArgs,
    bool Fresh,
    bool Manual,
    bool KeepOpen,
    int SettleSeconds)
{
    public const string DefaultStartUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";

    public static Options? Parse(string[] args)
    {
        var o = new Options("profile", Path.Combine("out", "cookies.txt"), "logs", DefaultStartUrl,
            null, null, Fresh: false, Manual: false, KeepOpen: false, SettleSeconds: 3);
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            try
            {
                o = args[i] switch
                {
                    "--profile" => o with { ProfileDir = Next() },
                    "--out" => o with { OutFile = Next() },
                    "--logs" => o with { LogDir = Next() },
                    "--start-url" => o with { StartUrl = Next() },
                    "--user-agent" => o with { UserAgent = Next() },
                    "--browser-args" => o with { BrowserArgs = Next() },
                    "--settle" => o with { SettleSeconds = int.Parse(Next(), CultureInfo.InvariantCulture) },
                    "--fresh" => o with { Fresh = true },
                    "--manual" => o with { Manual = true },
                    "--keep-open" => o with { KeepOpen = true },
                    _ => throw new ArgumentException($"Unknown argument {args[i]}"),
                };
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                Console.Error.WriteLine(ex.Message);
                return null;
            }
        }

        return o;
    }

    public static void PrintUsage() => Console.Error.WriteLine("""
        Usage: dotnet run -- [options]
          --fresh                 Delete the profile folder first (clean sign-in).
          --profile DIR           WebView2 user-data folder (default: profile).
          --out FILE              Netscape cookies file (default: out\cookies.txt).
          --logs DIR              Log folder (default: logs).
          --start-url URL         Page to open (default: Google sign-in, continue=youtube.com).
          --user-agent UA         Override the user agent.
          --browser-args ARGS     Extra Chromium switches for the WebView2 browser process.
          --settle SECONDS        Wait after login is detected before exporting (default: 3).
          --manual                Don't export on detection; use the "Export now" button.
          --keep-open             Don't close the WebView2 after exporting.
        """);
}

internal sealed class LoginWindow : Window
{
    private static readonly string[] LoginCookieNames = ["LOGIN_INFO", "SAPISID"];
    private static readonly string[] ExportDomains = ["youtube.com", "google.com"];

    private readonly Options _options;
    private readonly WebView2 _webView = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _loginLogged;
    private bool _exporting;

    public LoginWindow(Options options)
    {
        _options = options;
        Title = "Spike #31: WebView2 sign-in";
        Width = 1100;
        Height = 850;

        var exportButton = new Button { Content = "Export now", Margin = new Thickness(8, 4, 0, 4), Padding = new Thickness(8, 2, 8, 2) };
        exportButton.Click += async (_, _) => await GuardAsync(() => ExportAsync("manual button"));

        var bar = new DockPanel();
        DockPanel.SetDock(exportButton, Dock.Left);
        bar.Children.Add(exportButton);
        bar.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_webView);
        Content = root;

        Loaded += async (_, _) => await GuardAsync(InitializeAsync);
        _poll.Tick += async (_, _) => await GuardAsync(CheckLoginAsync);
    }

    public int ExitCode { get; private set; } = 1;

    private async Task InitializeAsync()
    {
        var profile = Path.GetFullPath(_options.ProfileDir);
        if (_options.Fresh && Directory.Exists(profile))
        {
            Directory.Delete(profile, recursive: true);
            Log.Write($"Deleted profile folder {profile}");
        }

        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: profile,
            options: new CoreWebView2EnvironmentOptions(_options.BrowserArgs));
        await _webView.EnsureCoreWebView2Async(env);
        var core = _webView.CoreWebView2;

        Log.Write($"WebView2 runtime: {env.BrowserVersionString}");
        Log.Write($"Profile folder:   {profile}");
        Log.Write($"Browser args:     {_options.BrowserArgs ?? "(none)"}");
        Log.Write($"Default UA:       {core.Settings.UserAgent}");
        if (_options.UserAgent is not null)
        {
            core.Settings.UserAgent = _options.UserAgent;
            Log.Write($"Overridden UA:    {core.Settings.UserAgent}");
        }

        core.NavigationStarting += (_, e) => Log.Write($"nav start  {Redact(e.Uri)}");
        core.NavigationCompleted += async (_, e) =>
        {
            Log.Write($"nav done   {Redact(core.Source)} ok={e.IsSuccess} http={e.HttpStatusCode} err={e.WebErrorStatus}");
            await GuardAsync(CheckLoginAsync);
        };
        core.NewWindowRequested += (_, e) => Log.Write($"popup      {Redact(e.Uri)}");
        core.ProcessFailed += (_, e) => Log.Write($"PROCESS FAILED: {e.ProcessFailedKind} {e.Reason}");

        _poll.Start();
        Log.Write($"Navigating to {Redact(_options.StartUrl)}");
        core.Navigate(_options.StartUrl);
    }

    private async Task CheckLoginAsync()
    {
        if (_exporting || _webView.CoreWebView2 is null)
        {
            return;
        }

        var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.youtube.com/");
        var names = cookies
            .Where(c => IsUnder(c.Domain, "youtube.com"))
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);
        var present = LoginCookieNames.Select(n => $"{n}: {(names.Contains(n) ? "yes" : "no")}");
        _status.Text = $"youtube.com cookies: {names.Count} | {string.Join(" | ", present)}";

        if (!LoginCookieNames.All(names.Contains))
        {
            return;
        }

        if (!_loginLogged)
        {
            _loginLogged = true;
            Log.Write($"Login detected: {string.Join(" and ", LoginCookieNames)} present on youtube.com");
        }

        if (_options.Manual)
        {
            _status.Text += " | login detected, press Export now";
            return;
        }

        _exporting = true;
        _poll.Stop();
        if (_options.SettleSeconds > 0)
        {
            Log.Write($"Waiting {_options.SettleSeconds}s for the redirect chain to settle");
            await Task.Delay(TimeSpan.FromSeconds(_options.SettleSeconds));
        }

        await ExportAsync("auto-detect");
    }

    private async Task ExportAsync(string reason)
    {
        _exporting = true;
        _poll.Stop();
        var core = _webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 not initialized");

        // An empty URI returns every cookie in the profile.
        var all = await core.CookieManager.GetCookiesAsync(string.Empty);
        var exported = all
            .Where(c => ExportDomains.Any(d => IsUnder(c.Domain, d)))
            .OrderBy(c => c.Domain, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var outFile = Path.GetFullPath(_options.OutFile);
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        await File.WriteAllTextAsync(outFile, ToNetscape(exported), new UTF8Encoding(false));
        var reportFile = Path.ChangeExtension(outFile, ".report.txt");
        await File.WriteAllTextAsync(reportFile, ToReport(all, exported), new UTF8Encoding(false));

        Log.Write($"Exported {exported.Count} of {all.Count} cookies ({reason}) to {outFile}");
        Log.Write($"Cookie report (names only, no values): {reportFile}");
        foreach (var group in all.Where(c => !exported.Contains(c)).GroupBy(c => c.Domain, StringComparer.Ordinal))
        {
            Log.Write($"  not exported: {group.Key} ({group.Count()} cookies)");
        }

        ExitCode = 0;
        if (_options.KeepOpen)
        {
            _status.Text = $"Exported {exported.Count} cookies. Window kept open (--keep-open).";
            return;
        }

        Log.Write("Closing the WebView2");
        _webView.Dispose();
        Close();
    }

    private static string ToNetscape(IEnumerable<CoreWebView2Cookie> cookies)
    {
        var sb = new StringBuilder();
        sb.Append("# Netscape HTTP Cookie File\n");
        sb.Append(CultureInfo.InvariantCulture, $"# Exported by spikes/webview2-login (issue #31) at {DateTimeOffset.UtcNow:O}. Do not commit.\n");
        foreach (var c in cookies)
        {
            // Chromium stores domain cookies with a leading dot; host-only cookies have none.
            var includeSubdomains = c.Domain.StartsWith('.');
            sb.Append(c.IsHttpOnly ? "#HttpOnly_" : string.Empty)
              .Append(c.Domain).Append('\t')
              .Append(includeSubdomains ? "TRUE" : "FALSE").Append('\t')
              .Append(c.Path).Append('\t')
              .Append(c.IsSecure ? "TRUE" : "FALSE").Append('\t')
              .Append(ExpiresUnix(c).ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(c.Name).Append('\t')
              .Append(c.Value).Append('\n');
        }

        return sb.ToString();
    }

    private static string ToReport(IReadOnlyCollection<CoreWebView2Cookie> all, IReadOnlyCollection<CoreWebView2Cookie> exported)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# {exported.Count} exported of {all.Count} total. Values omitted.\n");
        sb.Append("exported\tdomain\tname\tpath\tsecure\thttpOnly\tsameSite\tsession\texpiresUtc\tvalueLength\n");
        foreach (var c in all.OrderBy(c => c.Domain, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            var expires = c.IsSession ? "-" : DateTimeOffset.FromUnixTimeSeconds(ExpiresUnix(c)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            sb.Append(CultureInfo.InvariantCulture,
                $"{(exported.Contains(c) ? "yes" : "no")}\t{c.Domain}\t{c.Name}\t{c.Path}\t{c.IsSecure}\t{c.IsHttpOnly}\t{c.SameSite}\t{c.IsSession}\t{expires}\t{c.Value.Length}\n");
        }

        return sb.ToString();
    }

    private static long ExpiresUnix(CoreWebView2Cookie c) =>
        c.IsSession ? 0 : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds();

    private static bool IsUnder(string cookieDomain, string root)
    {
        var d = cookieDomain.TrimStart('.');
        return d.Equals(root, StringComparison.OrdinalIgnoreCase)
            || d.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);
    }

    // Query strings on Google sign-in URLs can carry tokens; log scheme, host and path only.
    private static string Redact(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : "(unparseable uri)";

    private static async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Write($"ERROR: {ex}");
        }
    }
}

internal static class Log
{
    private static StreamWriter? s_file;

    public static void Open(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"harness-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        s_file = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        Write($"Log file: {Path.GetFullPath(path)}");
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Console.WriteLine(line);
        s_file?.WriteLine(line);
    }
}
