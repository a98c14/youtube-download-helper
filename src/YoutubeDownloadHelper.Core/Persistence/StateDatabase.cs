using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace YoutubeDownloadHelper.Core.Persistence;

/// <summary>
/// Opens <c>state.db</c> at engine startup: creates the data folders, backs up an existing database before any
/// pending migration is applied, migrates, and switches the file to WAL (PLAN.md §4.3, §8).
/// </summary>
public sealed class StateDatabase(DataPaths paths, IDbContextFactory<StateDbContext> contextFactory, TimeProvider time)
{
    /// <summary>How many pre-migration backups are kept; older ones are deleted.</summary>
    public const int BackupsToKeep = 5;

    private const string BackupPrefix = "state-";
    private const string BackupExtension = ".db";

    /// <summary>
    /// The connection string for <paramref name="dbPath"/>. Connections are pooled, so callers that delete the
    /// file (tests) must call <see cref="SqliteConnection.ClearAllPools"/> first.
    /// </summary>
    public static string ConnectionString(string dbPath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();

    /// <summary>The backup taken by the last <see cref="InitializeAsync"/>, if any.</summary>
    public string? LastBackupPath { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var existed = File.Exists(paths.StateDb);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

        if (existed && pending.Count > 0)
        {
            LastBackupPath = await BackupAsync(db, pending[0], cancellationToken);
            PruneBackups();
        }

        await db.Database.MigrateAsync(cancellationToken);

        // journal_mode is stored in the file, so this is a no-op after the first start.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);

        await RecordSchemaInfoAsync(db, cancellationToken);
    }

    private async Task<string> BackupAsync(StateDbContext db, string firstPendingMigration, CancellationToken cancellationToken)
    {
        var stamp = time.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(paths.BackupsDir, $"{BackupPrefix}{stamp}-before-{firstPendingMigration}{BackupExtension}");

        // SQLite's online backup copies a consistent snapshot, including pages still in the WAL file.
        var source = (SqliteConnection)db.Database.GetDbConnection();
        await source.OpenAsync(cancellationToken);
        try
        {
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            target.Open();
            source.BackupDatabase(target);
        }
        finally
        {
            await source.CloseAsync();
        }

        return path;
    }

    /// <summary>Backup names start with a sortable UTC timestamp, so ordinal order is age order.</summary>
    private void PruneBackups()
    {
        var backups = Directory.GetFiles(paths.BackupsDir, $"{BackupPrefix}*{BackupExtension}")
            .Order(StringComparer.Ordinal)
            .ToList();

        foreach (var old in backups.Take(Math.Max(0, backups.Count - BackupsToKeep)))
        {
            File.Delete(old);
        }
    }

    private static async Task RecordSchemaInfoAsync(StateDbContext db, CancellationToken cancellationToken)
    {
        var version = typeof(StateDatabase).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";

        var rows = await db.SchemaInfo.ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken);
        Upsert(SchemaInfoKeys.LastOpenedByVersion);
        if (!rows.ContainsKey(SchemaInfoKeys.CreatedByVersion))
        {
            Upsert(SchemaInfoKeys.CreatedByVersion);
        }

        await db.SaveChangesAsync(cancellationToken);

        void Upsert(string key)
        {
            if (rows.TryGetValue(key, out var row))
            {
                row.Value = version;
            }
            else
            {
                db.SchemaInfo.Add(new SchemaInfoEntity { Key = key, Value = version });
            }
        }
    }
}
