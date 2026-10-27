// Fake headless engine: single instance, 127.0.0.1 HTTP with GET /info and POST /shutdown,
// and a heartbeat log so the log shows when (and how) it stopped.
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;

const int Port = 47123;
var version = Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YdhVelopackSpike");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, "engine.log");
void Log(string msg) =>
    File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} [engine {version} pid {Environment.ProcessId}] {msg}{Environment.NewLine}");

using var mutex = new Mutex(true, @"Local\YdhVelopackSpike.Engine", out var createdNew);
if (!createdNew)
{
    Log("another engine is running, exiting");
    return 0;
}

Log($"started from {Environment.ProcessPath}");
AppDomain.CurrentDomain.ProcessExit += (_, _) => Log("ProcessExit");

using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
listener.Start();

using var cts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        Log("heartbeat");
        try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); } catch (OperationCanceledException) { }
    }
});

while (!cts.IsCancellationRequested)
{
    var ctx = await listener.GetContextAsync();
    var path = ctx.Request.Url!.AbsolutePath;
    string body;
    if (path == "/info")
    {
        body = $$"""{"version":"{{version}}","pid":{{Environment.ProcessId}},"path":"{{Environment.ProcessPath!.Replace("\\", "\\\\")}}"}""";
    }
    else if (path == "/shutdown" && ctx.Request.HttpMethod == "POST")
    {
        body = "{\"ok\":true}";
        Log("shutdown requested");
        cts.Cancel();
    }
    else
    {
        ctx.Response.StatusCode = 404;
        body = "{}";
    }
    var bytes = Encoding.UTF8.GetBytes(body);
    ctx.Response.ContentType = "application/json";
    await ctx.Response.OutputStream.WriteAsync(bytes);
    ctx.Response.Close();
}

// Simulate a graceful drain (the real engine stops jobs and flushes SQLite here).
await Task.Delay(1000);
Log("clean exit");
return 0;
