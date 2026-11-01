using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Serilog;
using Serilog.Events;
using YoutubeDownloadHelper.Contracts.Messages;
using YoutubeDownloadHelper.Core.Auth;
using YoutubeDownloadHelper.Core.Errors;
using YoutubeDownloadHelper.Core.Persistence;
using YoutubeDownloadHelper.Core.Presets;
using YoutubeDownloadHelper.Core.Queue;
using YoutubeDownloadHelper.Core.Runner;
using YoutubeDownloadHelper.Core.Scheduling;
using YoutubeDownloadHelper.Core.Tools;
using YoutubeDownloadHelper.Engine.Endpoints;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// Builds the engine host (PLAN.md §6). <see cref="Program"/> and the integration tests share it; the tests swap
/// Kestrel for a TestServer and leave everything else as is.
/// </summary>
public static class EngineHost
{
    public const string LogFilePrefix = "engine-";

    public const string BearerSchemeId = "bearer";

    public static WebApplicationBuilder CreateBuilder(string[] args) =>
        WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

    public static EngineOptions ReadOptions(IConfiguration configuration) =>
        configuration.GetSection(EngineOptions.Section).Get<EngineOptions>() ?? new EngineOptions();

    public static DataPaths ResolveDataPaths(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.DataRoot) ? DataPaths.ForCurrentUser() : new DataPaths(options.DataRoot);
    }

    /// <summary>Registers the Core features, the API host services, logging and Kestrel on <c>127.0.0.1</c>.</summary>
    public static WebApplicationBuilder AddEngine(this WebApplicationBuilder builder, DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(paths);
        var services = builder.Services;

        services
            .AddPersistence(paths)
            .AddQueue()
            .AddErrors()
            .AddPresets()
            .AddTools(builder.Configuration.GetSection(EngineOptions.ToolsSection).Get<ToolLocatorOptions>())
            .AddToolManager()
            .AddRunner()
            .AddAuth()
            .AddScheduling();

        services.AddSingleton<ApiToken>();
        services.AddSingleton<LogRedactor>();
        services.AddSingleton<EngineEndpoint>();
        services.AddHostedService<DiscoveryService>();
        services.AddHostedService<SchedulerService>();
        services.AddHostedService<ToolManagerService>();

        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
        {
            DescribeDocument(document);
            return Task.CompletedTask;
        }));

        // Only 127.0.0.1 (bound in StartAsync): the Kestrel configuration section and URL settings are ignored, so no
        // setting can expose the engine on the network.
        builder.WebHost.UseSetting(WebHostDefaults.PreferHostingUrlsKey, "false");
        services.Configure<KestrelServerOptions>(o =>
        {
            o.Configure(new ConfigurationBuilder().Build());
            o.AddServerHeader = false;
        });

        // preserveStaticLogger: each host owns (and disposes) its logger; Serilog's static Log.Logger stays unused.
        services.AddSerilog((sp, logger) => ConfigureLogging(logger, paths, sp.GetRequiredService<LogRedactor>()), preserveStaticLogger: true);
        return builder;
    }

    /// <summary>The middleware and the endpoints. Every route, the OpenAPI document included, is guarded.</summary>
    public static WebApplication MapEngine(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = ApiErrors.WriteInternalAsync });
        app.UseStatusCodePages(ApiErrors.WriteForStatusAsync);
        app.UseMiddleware<RequestGuardMiddleware>();

        app.MapOpenApi();

        var api = app.MapGroup(string.Empty)
            .WithMetadata(
                new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized, typeof(EngineMessage), ["application/json"]),
                new ProducesResponseTypeMetadata(StatusCodes.Status403Forbidden, typeof(EngineMessage), ["application/json"]));
        api.MapInfoEndpoints();
        api.MapEngineEndpoints();
        return app;
    }

    /// <summary>
    /// Opens the database, loads the installed tool versions, picks the port (the <c>Engine:Port</c> override, else the
    /// <c>ApiPort</c> setting), starts the queue, then starts the host (and with it the scheduler and the tool manager's
    /// health check). The discovery file is written once the server listens.
    /// </summary>
    public static async Task StartAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        var services = app.Services;

        await services.GetRequiredService<StateDatabase>().InitializeAsync(cancellationToken);
        await services.GetRequiredService<ToolManager>().InitializeAsync(cancellationToken);
        var settings = await services.GetRequiredService<SettingsStore>().LoadAsync(cancellationToken);
        var endpoint = services.GetRequiredService<EngineEndpoint>();
        endpoint.RequestedPort = ReadOptions(app.Configuration).Port ?? settings.ApiPort;

        // Kestrel reads its listen options when it starts, from this same options instance. (A TestServer ignores it.)
        services.GetRequiredService<IOptions<KestrelServerOptions>>().Value
            .Listen(IPAddress.Loopback, PortProbe.AvailableOrDynamic(endpoint.RequestedPort));
        await services.GetRequiredService<QueueActor>().StartAsync(cancellationToken);

        await app.StartAsync(cancellationToken);
    }

    /// <summary>Rolling <c>logs\engine-&lt;yyyyMMdd&gt;.log</c>, kept 14 days, invariant formatting, every event redacted.</summary>
    public static LoggerConfiguration ConfigureLogging(LoggerConfiguration logger, DataPaths paths, LogRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(paths);
        return logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.With(new RedactingEnricher(redactor))
            .WriteTo.File(
                Path.Combine(paths.LogsDir, LogFilePrefix + ".log"),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(14),
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void DescribeDocument(OpenApiDocument document)
    {
        document.Info.Title = "YoutubeDownloadHelper engine API";
        document.Info.Version = "1";
        document.Info.Description = "Base URL and bearer token: the engine.json discovery file in the data folder.";

        // The port differs per run; clients take the base URL from the discovery file.
        document.Servers = [];

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[BearerSchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "The token from engine.json. Regenerated on every engine start.",
        };
        document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerSchemeId, document)] = [] }];
    }
}
