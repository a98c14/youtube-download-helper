using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Core.Tests.Persistence;

/// <summary>
/// A throwaway data folder whose path contains Turkish dotted/dotless I (and other non-ASCII), wired up with
/// <see cref="PersistenceServiceCollectionExtensions.AddPersistence"/>.
/// </summary>
public sealed class TestDataRoot : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    public TestDataRoot()
    {
        Paths = new DataPaths(Path.Combine(Path.GetTempPath(), "YoutubeDownloadHelper.Tests", $"İndirgeç ılık {Guid.NewGuid():N}"));
        Defaults = EngineSettings.Defaults(Path.Combine(Paths.Root, "profile"));
        _services = new ServiceCollection()
            .AddSingleton<TimeProvider>(Time)
            .AddPersistence(Paths, Defaults)
            .BuildServiceProvider();
    }

    public DataPaths Paths { get; }

    public EngineSettings Defaults { get; }

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    public StateDatabase Database => _services.GetRequiredService<StateDatabase>();

    public SettingsStore Settings => _services.GetRequiredService<SettingsStore>();

    public StateDbContext CreateContext() =>
        _services.GetRequiredService<IDbContextFactory<StateDbContext>>().CreateDbContext();

    public async Task<TestDataRoot> InitializedAsync()
    {
        await Database.InitializeAsync(TestContext.Current.CancellationToken);
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();

        // Pooled connections keep the file open, which blocks deleting it on Windows.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Paths.Root))
        {
            Directory.Delete(Paths.Root, recursive: true);
        }
    }
}

public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
