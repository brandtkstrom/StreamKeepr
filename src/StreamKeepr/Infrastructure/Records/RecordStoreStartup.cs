using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

namespace StreamKeepr.Infrastructure.Records;

/// <summary>
/// Opens, checks, backs up and migrates the record store before any store loads (ADR-0014).
/// Register it as the hosted service right after preflight. Every failure stops startup with a
/// <see cref="RecordStoreException"/> naming the path and the reason; the database is never
/// reset, and nothing is written to it until the checks and the backup have succeeded. The file
/// and its backups are owner-only: they will hold the admin token's ciphertext (#319).
/// </summary>
public sealed class RecordStoreStartup(
    IOptions<StorageOptions> storage,
    IDbContextFactory<RecordStoreDbContext> contexts,
    RecordStoreState state,
    TimeProvider clock,
    ILogger<RecordStoreStartup> logger) : IHostedService
{
    public const string BackupDirectoryName = "backups";
    public const int BackupsKept = 3;

    private const string BackupPrefix = "streamkeepr-";
    private const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Sidecars the record store replaces. They are never read or imported; startup only says
    /// that they were found. Later tickets add theirs as they move onto the store.
    /// </summary>
    private static readonly string[] LegacyFileNames =
    [
        AppDataPaths.ChannelsFileName,
        AppDataPaths.AdminTokensFileName,
        AppDataPaths.EventSubConduitFileName,
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(storage.Value.DatabasePath);
        string[] known = KnownMigrations();
        bool existed = File.Exists(path) || Directory.Exists(path);

        string[] applied = existed
            ? await CheckExistingAsync(path, known, cancellationToken)
            : [];
        string[] pending = [.. known.Except(applied, StringComparer.Ordinal)];

        if (existed && pending.Length > 0)
            await BackUpAsync(path, pending[^1], cancellationToken);

        // The first write: everything before it only read the file.
        await UseWriteAheadLogAsync(path, create: !existed, cancellationToken);

        if (pending.Length > 0)
            await MigrateAsync(path, pending, cancellationToken);

        state.MarkPrepared();
        LogIgnoredLegacyFiles();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string[] KnownMigrations()
    {
        using RecordStoreDbContext db = contexts.CreateDbContext();
        return [.. db.Database.GetMigrations()];
    }

    /// <summary>
    /// Opens the existing file without applying the connection pragmas (switching to WAL would
    /// write to it), runs quick_check, and reads the applied migrations. Reads only.
    /// </summary>
    private static async Task<string[]> CheckExistingAsync(string path, string[] known, CancellationToken ct)
    {
        List<string> applied;
        try
        {
            await using var connection = new SqliteConnection(
                RecordStoreConnection.ConnectionString(path, SqliteOpenMode.ReadWrite));
            await connection.OpenAsync(ct);

            string[] problems = await QuickCheckAsync(connection, ct);
            if (problems is not ["ok"])
            {
                throw Failure(
                    path,
                    $"failed its integrity check (PRAGMA quick_check): {string.Join("; ", problems.Take(5))}");
            }

            applied = await AppliedMigrationsAsync(connection, ct);
        }
        catch (SqliteException ex)
        {
            throw Failure(path, $"cannot be opened: {ex.Message}", ex);
        }

        string[] unknown = [.. applied.Except(known, StringComparer.Ordinal)];
        if (unknown.Length > 0)
        {
            throw Failure(
                path,
                $"is newer than this version of Stream Keepr: it has migrations this build does not know " +
                $"({string.Join(", ", unknown)}). Run a newer version or restore a backup");
        }

        return [.. applied];
    }

    /// <summary>
    /// The quick_check rows: exactly <c>ok</c> when the file is sound. Damage bad enough that
    /// SQLite aborts the check (corrupt or not a database) is reported as a failed check too.
    /// </summary>
    private static async Task<string[]> QuickCheckAsync(SqliteConnection connection, CancellationToken ct)
    {
        const int SqliteCorrupt = 11;
        const int SqliteNotADatabase = 26;

        try
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var rows = new List<string>();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(reader.GetString(0));
            return [.. rows];
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            return [ex.Message];
        }
    }

    private static async Task<List<string>> AppliedMigrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteCommand exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        exists.Parameters.AddWithValue("$name", MigrationsHistoryTable);
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0)
            return [];

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT MigrationId FROM \"{MigrationsHistoryTable}\" ORDER BY MigrationId;";
        var applied = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            applied.Add(reader.GetString(0));
        return applied;
    }

    /// <summary>A consistent copy via VACUUM INTO, then only the newest <see cref="BackupsKept"/> stay.</summary>
    private async Task BackUpAsync(string path, string targetMigration, CancellationToken ct)
    {
        string directory = Path.Combine(Path.GetDirectoryName(path)!, BackupDirectoryName);
        string stamp = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        string backup = Path.Combine(directory, $"{BackupPrefix}{stamp}-pre-{targetMigration}.db");

        try
        {
            OwnerOnlyFileSystem.EnsurePrivateDirectory(directory);
            await using var connection = new SqliteConnection(
                RecordStoreConnection.ConnectionString(path, SqliteOpenMode.ReadWrite));
            await connection.OpenAsync(ct);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $backup;";
            command.Parameters.AddWithValue("$backup", backup);
            await command.ExecuteNonQueryAsync(ct);
            OwnerOnlyFileSystem.SetOwnerOnlyFile(backup);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw Failure(path, $"could not be backed up to '{backup}' before migrating: {ex.Message}", ex);
        }

        logger.LogInformation(
            "Backed up the record store at {Path} to {Backup} before applying {Migration}.",
            path,
            backup,
            targetMigration);
        PruneBackups(directory);
    }

    private void PruneBackups(string directory)
    {
        // The UTC stamp sorts the names chronologically.
        string[] stale =
        [
            .. Directory.GetFiles(directory, $"{BackupPrefix}*-pre-*.db")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(BackupsKept),
        ];

        foreach (string file in stale)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A kept extra backup costs only space; the new one is already written.
                logger.LogWarning(ex, "Could not remove the old record store backup {Backup}.", file);
            }
        }
    }

    /// <summary>
    /// Switches the file to WAL, which persists, and creates it on a fresh start: the only
    /// place that may, since contexts open without create. SQLite gives the -wal and -shm files
    /// the database file's owner-only mode.
    /// </summary>
    private static async Task UseWriteAheadLogAsync(string path, bool create, CancellationToken ct)
    {
        try
        {
            if (create)
                OwnerOnlyFileSystem.CreateOwnerOnlyFile(path);

            await using var connection = new SqliteConnection(
                RecordStoreConnection.ConnectionString(path, SqliteOpenMode.ReadWrite));
            await connection.OpenAsync(ct);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = RecordStoreConnection.JournalMode;
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw Failure(path, $"cannot be opened: {ex.Message}", ex);
        }
    }

    private async Task MigrateAsync(string path, string[] pending, CancellationToken ct)
    {
        try
        {
            await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
            await db.Database.MigrateAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Failure(path, $"could not be migrated ({string.Join(", ", pending)}): {ex.Message}", ex);
        }

        logger.LogInformation(
            "Applied {Count} record store migrations to {Path}: {Migrations}.",
            pending.Length,
            path,
            pending);
    }

    private void LogIgnoredLegacyFiles()
    {
        string appData = AppDataPaths.AppDataDirectory(storage.Value.RecordingsRoot);
        string[] found =
        [
            .. LegacyFileNames.Where(name => File.Exists(Path.Combine(appData, name))),
        ];

        if (found.Length > 0)
        {
            logger.LogWarning(
                "Found legacy files under {Directory} ({Files}); they are ignored and never imported (ADR-0014).",
                appData,
                string.Join(", ", found));
        }
    }

    private static RecordStoreException Failure(string path, string reason, Exception? inner = null) =>
        new($"The record store at '{path}' {reason}. Startup stopped; the database was not reset.", inner);
}
