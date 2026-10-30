using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using YoutubeDownloadHelper.Contracts.Queue;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>
/// <c>state.db</c> (PLAN.md §8). Every table is created here up front, including the ones later issues fill.
/// Names are snake_case; enums are stored by name; timestamps are UTC Unix milliseconds so SQLite can compare
/// and sort them.
/// <para>
/// There is no archive table: items are never hard-deleted, and those with a file are the download archive
/// (<see cref="ItemQueries.GetDownloadedVideoIdsAsync"/>).
/// </para>
/// </summary>
public sealed class StateDbContext(DbContextOptions<StateDbContext> options) : DbContext(options)
{
    public DbSet<ItemEntity> Items => Set<ItemEntity>();

    public DbSet<ItemEventEntity> ItemEvents => Set<ItemEventEntity>();

    public DbSet<WatcherEntity> Watchers => Set<WatcherEntity>();

    public DbSet<WatcherSeenEntity> WatcherSeen => Set<WatcherSeenEntity>();

    public DbSet<SettingEntity> Settings => Set<SettingEntity>();

    public DbSet<ToolEntity> Tools => Set<ToolEntity>();

    public DbSet<SchemaInfoEntity> SchemaInfo => Set<SchemaInfoEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();
        configurationBuilder.Properties<ItemState>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ItemEntity>(e =>
        {
            e.ToTable("items");
            e.HasIndex(i => new { i.State, i.Position });
            e.HasIndex(i => new { i.Extractor, i.VideoId });
            e.HasIndex(i => new { i.FinishedAt, i.Id });
            e.HasOne(i => i.Watcher).WithMany().HasForeignKey(i => i.WatcherId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(i => i.Events).WithOne().HasForeignKey(ev => ev.ItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ItemEventEntity>(e =>
        {
            e.ToTable("item_events");
            e.HasIndex(ev => new { ev.ItemId, ev.Id });
        });

        modelBuilder.Entity<WatcherEntity>(e =>
        {
            e.ToTable("watchers");
            e.HasMany(w => w.Seen).WithOne().HasForeignKey(s => s.WatcherId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WatcherSeenEntity>(e =>
        {
            e.ToTable("watcher_seen");
            e.HasKey(s => new { s.WatcherId, s.Extractor, s.VideoId });
        });

        modelBuilder.Entity<SettingEntity>(e =>
        {
            e.ToTable("settings");
            e.HasKey(s => s.Key);
        });

        modelBuilder.Entity<ToolEntity>(e =>
        {
            e.ToTable("tools");
            e.HasKey(t => t.Tool);
        });

        modelBuilder.Entity<SchemaInfoEntity>(e =>
        {
            e.ToTable("schema_info");
            e.HasKey(s => s.Key);
        });

        ApplySnakeCaseNames(modelBuilder);
    }

    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName($"pk_{table}");
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                fk.SetConstraintName($"fk_{table}_{fk.PrincipalEntityType.GetTableName()}");
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName($"ix_{table}_{string.Join('_', index.Properties.Select(p => ToSnakeCase(p.Name)))}");
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private sealed class UnixMillisecondsConverter()
        : ValueConverter<DateTimeOffset, long>(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));
}
