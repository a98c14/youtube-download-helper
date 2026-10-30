using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace YoutubeDownloadHelper.Core.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DataPaths"/>, the <see cref="StateDbContext"/> factory, <see cref="StateDatabase"/> and
    /// <see cref="SettingsStore"/>. The host calls <see cref="StateDatabase.InitializeAsync"/> once before anything
    /// reads the database. Tests pass their own <paramref name="paths"/> and <paramref name="defaults"/>.
    /// </summary>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        DataPaths? paths = null,
        EngineSettings? defaults = null)
    {
        paths ??= DataPaths.ForCurrentUser();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(paths);
        services.AddSingleton(defaults ?? EngineSettings.DefaultsForCurrentUser());
        services.AddDbContextFactory<StateDbContext>(o => o.UseSqlite(StateDatabase.ConnectionString(paths.StateDb)));
        services.AddSingleton<StateDatabase>();
        services.AddSingleton<SettingsStore>();
        return services;
    }
}
