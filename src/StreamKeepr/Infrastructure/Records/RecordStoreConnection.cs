using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace StreamKeepr.Infrastructure.Records;

/// <summary>
/// How every record-store connection is opened and configured. Contexts open with
/// <see cref="SqliteOpenMode.ReadWrite"/>, so only <see cref="RecordStoreStartup"/> can create
/// the file; a database removed at runtime fails loudly instead of being silently recreated.
/// </summary>
public static class RecordStoreConnection
{
    public const int BusyTimeoutMilliseconds = 5_000;

    /// <summary>
    /// Persists in the file, so <see cref="RecordStoreStartup"/> sets it once, after its checks
    /// and backup. It is not in <see cref="Pragmas"/>: EF opens read-only connections (to test
    /// that the file exists), and switching the journal mode there fails.
    /// </summary>
    public const string JournalMode = "PRAGMA journal_mode = WAL;";

    /// <summary>Per-connection settings, so they are applied on every open.</summary>
    public static readonly string Pragmas =
        "PRAGMA foreign_keys = ON;" +
        $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};" +
        "PRAGMA synchronous = FULL;" +
        "PRAGMA secure_delete = ON;";

    /// <summary>
    /// Pooling is off: the store is low-volume, every open re-applies <see cref="Pragmas"/>,
    /// and no idle handle keeps the file open after the host stops (the Mock profile deletes it).
    /// </summary>
    public static string ConnectionString(string databasePath, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = mode,
            Pooling = false,
        }.ToString();

    public static void ApplyPragmas(DbConnection connection)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Copies every write-ahead-log frame into the database and empties the log, so page images
    /// written before a deletion (which <c>secure_delete</c> cannot reach) are gone from disk.
    /// Returns false when a reader still in a transaction kept the log from being emptied; the
    /// old frames are then overwritten by later writes once SQLite restarts the log.
    /// </summary>
    public static bool CheckpointWriteAheadLog(DbConnection connection)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using DbDataReader result = command.ExecuteReader();

        // One row: busy (0 or 1), frames in the log, frames checkpointed.
        return result.Read() && result.GetInt32(0) == 0;
    }

    public static async Task ApplyPragmasAsync(DbConnection connection, CancellationToken ct)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Applies <see cref="RecordStoreConnection.Pragmas"/> to each connection EF opens.</summary>
public sealed class RecordStorePragmas : DbConnectionInterceptor
{
    public static readonly RecordStorePragmas Instance = new();

    private RecordStorePragmas()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        RecordStoreConnection.ApplyPragmas(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        RecordStoreConnection.ApplyPragmasAsync(connection, cancellationToken);
}
