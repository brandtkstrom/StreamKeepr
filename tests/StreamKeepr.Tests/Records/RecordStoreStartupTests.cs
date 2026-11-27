using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Records;

public sealed class RecordStoreStartupTests : IDisposable
{
    private readonly TestRecordStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task Fresh_start_creates_and_migrates_the_database_without_a_backup()
    {
        RecordStoreState state = await _store.StartAsync();

        Assert.True(File.Exists(_store.DatabasePath));
        Assert.True(state.Prepared);
        await using RecordStoreDbContext db = await _store.Contexts.CreateDbContextAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.False(Directory.Exists(_store.BackupDirectory));
    }

    [Fact]
    public async Task The_database_and_its_backups_are_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return;

        await CreateLegacyDatabaseAsync();
        File.SetUnixFileMode(_store.DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await _store.StartAsync();
        string backup = Assert.Single(Directory.GetFiles(_store.BackupDirectory));

        using var fresh = new TestRecordStore();
        await fresh.StartAsync();

        const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(fresh.DatabasePath));
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(backup));
        Assert.Equal(
            OwnerOnlyFile | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_store.BackupDirectory));
    }

    [Fact]
    public async Task Restart_of_a_current_database_creates_nothing_and_writes_no_backup()
    {
        await _store.StartAsync();

        RecordStoreState restart = await _store.StartAsync();

        Assert.True(restart.Prepared);
        Assert.False(Directory.Exists(_store.BackupDirectory));
    }

    [Fact]
    public async Task Existing_database_with_pending_migrations_is_backed_up_before_migrating()
    {
        await CreateLegacyDatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 30, 45, 123, TimeSpan.Zero));

        await _store.StartAsync(clock);

        string backup = Assert.Single(Directory.GetFiles(_store.BackupDirectory));
        Assert.Equal(
            $"streamkeepr-20261127T123045123Z-pre-{LatestMigration()}.db",
            Path.GetFileName(backup));
        Assert.Equal(["Marker"], await TablesAsync(backup));
        await using RecordStoreDbContext db = await _store.Contexts.CreateDbContextAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Only_the_three_most_recent_backups_are_kept()
    {
        Directory.CreateDirectory(_store.BackupDirectory);
        string[] older =
        [
            "streamkeepr-20240101T000000000Z-pre-x.db",
            "streamkeepr-20250101T000000000Z-pre-x.db",
            "streamkeepr-20260101T000000000Z-pre-x.db",
        ];
        foreach (string name in older)
            await File.WriteAllTextAsync(Path.Combine(_store.BackupDirectory, name), "old");
        string unrelated = Path.Combine(_store.BackupDirectory, "notes.txt");
        await File.WriteAllTextAsync(unrelated, "keep me");
        await CreateLegacyDatabaseAsync();

        await _store.StartAsync(new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(
            [
                "notes.txt",
                "streamkeepr-20250101T000000000Z-pre-x.db",
                "streamkeepr-20260101T000000000Z-pre-x.db",
                $"streamkeepr-20261127T000000000Z-pre-{LatestMigration()}.db",
            ],
            Directory.GetFiles(_store.BackupDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_failed_backup_stops_startup_before_any_migration_runs()
    {
        await CreateLegacyDatabaseAsync();
        // A file where the backup directory belongs makes the backup impossible.
        await File.WriteAllTextAsync(_store.BackupDirectory, "in the way");
        byte[] before = Hash(_store.DatabasePath);

        RecordStoreException error = await Assert.ThrowsAsync<RecordStoreException>(() => _store.StartAsync());

        Assert.Contains(_store.DatabasePath, error.Message, StringComparison.Ordinal);
        Assert.Contains("backup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Hash(_store.DatabasePath));
        Assert.Equal(["Marker"], await TablesAsync(_store.DatabasePath));
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_stops_startup_and_is_left_unchanged()
    {
        await File.WriteAllTextAsync(_store.DatabasePath, "this is not a SQLite database, just some text");
        byte[] before = Hash(_store.DatabasePath);

        RecordStoreException error = await Assert.ThrowsAsync<RecordStoreException>(() => _store.StartAsync());

        Assert.Contains(_store.DatabasePath, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Hash(_store.DatabasePath));
        Assert.False(Directory.Exists(_store.BackupDirectory));
    }

    [Fact]
    public async Task A_database_that_fails_quick_check_stops_startup_and_is_left_unchanged()
    {
        await CreateCorruptDatabaseAsync();
        byte[] before = Hash(_store.DatabasePath);

        RecordStoreException error = await Assert.ThrowsAsync<RecordStoreException>(() => _store.StartAsync());

        Assert.Contains(_store.DatabasePath, error.Message, StringComparison.Ordinal);
        Assert.Contains("quick_check", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Hash(_store.DatabasePath));
        Assert.False(Directory.Exists(_store.BackupDirectory));
    }

    [Fact]
    public async Task A_database_path_that_cannot_be_opened_stops_startup()
    {
        Directory.CreateDirectory(_store.DatabasePath);

        RecordStoreException error = await Assert.ThrowsAsync<RecordStoreException>(() => _store.StartAsync());

        Assert.Contains(_store.DatabasePath, error.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be opened", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_database_newer_than_the_app_stops_startup_and_is_left_unchanged()
    {
        await _store.StartAsync();
        await using (var connection = await OpenAsync(_store.DatabasePath))
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) " +
                "VALUES ('29991231000000_FromTheFuture', '99.0.0');");
        }

        byte[] before = Hash(_store.DatabasePath);

        RecordStoreException error = await Assert.ThrowsAsync<RecordStoreException>(() => _store.StartAsync());

        Assert.Contains(_store.DatabasePath, error.Message, StringComparison.Ordinal);
        Assert.Contains("29991231000000_FromTheFuture", error.Message, StringComparison.Ordinal);
        Assert.Contains("newer", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Hash(_store.DatabasePath));
    }

    [Fact]
    public async Task Connections_use_wal_foreign_keys_busy_timeout_full_sync_and_secure_delete()
    {
        await _store.StartAsync();

        await using RecordStoreDbContext db = await _store.Contexts.CreateDbContextAsync();
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();

        Assert.Equal("wal", await PragmaAsync(connection, "journal_mode"));
        Assert.Equal("1", await PragmaAsync(connection, "foreign_keys"));
        Assert.Equal("5000", await PragmaAsync(connection, "busy_timeout"));
        Assert.Equal("2", await PragmaAsync(connection, "synchronous")); // FULL
        Assert.Equal("1", await PragmaAsync(connection, "secure_delete"));
    }

    [Fact]
    public async Task Legacy_channel_list_is_ignored_with_exactly_one_log_line()
    {
        Directory.CreateDirectory(AppDataPaths.AppDataDirectory(_store.RecordingsRoot));
        string legacy = AppDataPaths.ChannelsFile(_store.RecordingsRoot);
        await File.WriteAllTextAsync(legacy, "[{\"login\":\"legacy\"}]");

        await _store.StartAsync();

        CapturedLogEntry line = Assert.Single(
            _store.Logs.Entries,
            entry => entry.Message.Contains("ignored", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("channels.json", line.Message, StringComparison.Ordinal);
        Assert.Equal("[{\"login\":\"legacy\"}]", await File.ReadAllTextAsync(legacy));
    }

    [Fact]
    public async Task No_legacy_files_means_no_ignored_line()
    {
        await _store.StartAsync();

        Assert.DoesNotContain(
            _store.Logs.Entries,
            entry => entry.Message.Contains("ignored", StringComparison.Ordinal));
    }

    private string LatestMigration()
    {
        using RecordStoreDbContext db = _store.Contexts.CreateDbContext();
        return db.Database.GetMigrations().Last();
    }

    /// <summary>An existing SQLite file that predates every migration this build knows.</summary>
    private async Task CreateLegacyDatabaseAsync()
    {
        await using SqliteConnection connection = await OpenAsync(_store.DatabasePath);
        await ExecuteAsync(connection, "CREATE TABLE Marker (Value TEXT); INSERT INTO Marker VALUES ('before');");
    }

    /// <summary>A valid header with damaged b-tree pages, which quick_check reports.</summary>
    private async Task CreateCorruptDatabaseAsync()
    {
        await using (SqliteConnection connection = await OpenAsync(_store.DatabasePath))
        {
            await ExecuteAsync(
                connection,
                "PRAGMA page_size = 4096;" +
                "CREATE TABLE Filler (Id INTEGER PRIMARY KEY, Value TEXT);" +
                "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000) " +
                "INSERT INTO Filler SELECT i, printf('%0100d', i) FROM n;");
        }

        await using FileStream file = File.Open(_store.DatabasePath, FileMode.Open, FileAccess.ReadWrite);
        Assert.True(file.Length > 4096 * 8);
        file.Seek(4096 * 3, SeekOrigin.Begin);
        file.Write(Enumerable.Repeat((byte)0xA5, 4096 * 4).ToArray());
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> TablesAsync(string path)
    {
        await using SqliteConnection connection = await OpenAsync(path);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));
        return [.. tables];
    }

    private static async Task<string?> PragmaAsync(System.Data.Common.DbConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
}
