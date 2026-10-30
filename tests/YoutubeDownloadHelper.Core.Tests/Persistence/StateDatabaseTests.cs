using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using YoutubeDownloadHelper.Core.Persistence;

namespace YoutubeDownloadHelper.Core.Tests.Persistence;

public sealed class StateDatabaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fresh_database_is_created_and_migrated_in_a_non_ASCII_folder_with_WAL_on()
    {
        await using var root = new TestDataRoot();
        Assert.Contains('İ', root.Paths.Root);
        Assert.Contains('ı', root.Paths.Root);

        await root.Database.InitializeAsync(Ct);

        Assert.True(File.Exists(root.Paths.StateDb));
        Assert.Null(root.Database.LastBackupPath);
        Assert.Empty(Directory.GetFiles(root.Paths.BackupsDir));

        await using var db = root.CreateContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.Equal("wal", await ScalarAsync(db, "PRAGMA journal_mode;"));
        Assert.Equal(
            ["item_events", "items", "schema_info", "settings", "tools", "watcher_seen", "watchers"],
            await TablesAsync(db));
        Assert.Equal(2, await db.SchemaInfo.CountAsync(Ct));
    }

    [Fact]
    public async Task Data_folders_are_created()
    {
        await using var root = await new TestDataRoot().InitializedAsync();

        foreach (var dir in new[] { root.Paths.BackupsDir, root.Paths.AuthDir, root.Paths.ToolsDir, root.Paths.WorkDir, root.Paths.JobLogsDir })
        {
            Assert.True(Directory.Exists(dir), dir);
        }
    }

    [Fact]
    public async Task Existing_database_is_backed_up_before_a_pending_migration_is_applied()
    {
        await using var root = new TestDataRoot();
        Directory.CreateDirectory(root.Paths.Root);
        await ExecuteAsync(root.Paths.StateDb, "CREATE TABLE sentinel (value TEXT); INSERT INTO sentinel VALUES ('before migration');");

        await root.Database.InitializeAsync(Ct);

        var backup = root.Database.LastBackupPath;
        Assert.NotNull(backup);
        Assert.Equal(root.Paths.BackupsDir, Path.GetDirectoryName(backup));
        Assert.EndsWith("_Initial.db", backup, StringComparison.Ordinal);

        // The backup is the pre-migration file: it has the sentinel row and no migration history.
        Assert.Equal("before migration", await ScalarAsync(backup, "SELECT value FROM sentinel;"));
        Assert.Equal(0L, await ScalarAsync(backup, "SELECT count(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory';"));

        await using var db = root.CreateContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task No_backup_is_taken_when_no_migration_is_pending()
    {
        await using var root = await new TestDataRoot().InitializedAsync();

        root.Time.Now = root.Time.Now.AddMinutes(1);
        await root.Database.InitializeAsync(Ct);

        Assert.Null(root.Database.LastBackupPath);
        Assert.Empty(Directory.GetFiles(root.Paths.BackupsDir));
    }

    [Fact]
    public async Task Only_the_newest_backups_are_kept()
    {
        await using var root = new TestDataRoot();
        root.Paths.EnsureCreated();
        var old = Enumerable.Range(1, StateDatabase.BackupsToKeep + 1)
            .Select(i => Path.Combine(root.Paths.BackupsDir, $"state-2026010{i}T000000000Z-before-X.db"))
            .ToList();
        old.ForEach(p => File.WriteAllText(p, ""));
        var unrelated = Path.Combine(root.Paths.BackupsDir, "notes.txt");
        File.WriteAllText(unrelated, "");
        await ExecuteAsync(root.Paths.StateDb, "CREATE TABLE sentinel (value TEXT);");

        await root.Database.InitializeAsync(Ct);

        var kept = Directory.GetFiles(root.Paths.BackupsDir, "state-*.db").Order(StringComparer.Ordinal).ToList();
        Assert.Equal([.. old.TakeLast(StateDatabase.BackupsToKeep - 1), root.Database.LastBackupPath!], kept);
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task Created_by_version_survives_later_starts()
    {
        await using var root = await new TestDataRoot().InitializedAsync();
        await using (var db = root.CreateContext())
        {
            var created = await db.SchemaInfo.SingleAsync(r => r.Key == SchemaInfoKeys.CreatedByVersion, Ct);
            created.Value = "0.0.1";
            await db.SaveChangesAsync(Ct);
        }

        await root.Database.InitializeAsync(Ct);

        await using var check = root.CreateContext();
        Assert.Equal("0.0.1", (await check.SchemaInfo.SingleAsync(r => r.Key == SchemaInfoKeys.CreatedByVersion, Ct)).Value);
        Assert.NotEqual("0.0.1", (await check.SchemaInfo.SingleAsync(r => r.Key == SchemaInfoKeys.LastOpenedByVersion, Ct)).Value);
    }

    /// <summary>Fails when an entity changed without regenerating the Initial migration.</summary>
    [Fact]
    public async Task Model_has_no_changes_missing_from_the_migration()
    {
        await using var root = new TestDataRoot();
        await using var db = root.CreateContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task<List<string>> TablesAsync(StateDbContext db) =>
        await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND substr(name, 1, 4) <> '__EF' ORDER BY name")
            .ToListAsync(Ct);

    private static async Task<object?> ScalarAsync(StateDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(string dbPath, string sql)
    {
        await using var connection = Open(dbPath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    private static async Task ExecuteAsync(string dbPath, string sql)
    {
        await using var connection = Open(dbPath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static SqliteConnection Open(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
