using YoutubeDownloadHelper.Contracts.Discovery;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        EngineCulture.Apply();

        var builder = EngineHost.CreateBuilder(args);
        var paths = EngineHost.ResolveDataPaths(EngineHost.ReadOptions(builder.Configuration));

        // Before anything touches the data folder: a second engine leaves it alone.
        using var instance = SingleInstance.TryAcquire(SingleInstance.MutexName(paths));
        if (instance is null)
        {
            return EngineExitCodes.AlreadyRunning;
        }

        await using var app = builder.AddEngine(paths).Build();
        app.MapEngine();

        try
        {
            await EngineHost.StartAsync(app);
            await app.WaitForShutdownAsync();
            return EngineExitCodes.Success;
        }
        catch (Exception ex)
        {
            LogFatal(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Program)), ex);
            return EngineExitCodes.Failed;
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "The engine failed")]
    private static partial void LogFatal(ILogger logger, Exception exception);
}
