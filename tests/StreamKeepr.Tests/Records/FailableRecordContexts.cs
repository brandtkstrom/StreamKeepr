using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Tests.Records;

/// <summary>
/// A real record-store context factory that can be told to fail the way a broken database
/// volume does. <see cref="Failing"/> makes every context throw SQLite's disk I/O error on
/// creation, so reads fail too. <see cref="WritesFailing"/> opens the same database read-only:
/// reads still work and every write raises SQLite's own "readonly database" error, which is
/// what exercises the order of a store's reads and writes.
/// </summary>
internal sealed class FailableRecordContexts : IDbContextFactory<RecordStoreDbContext>
{
    private const int SqliteIoError = 10;

    private readonly IDbContextFactory<RecordStoreDbContext> _inner;
    private readonly DbContextOptions<RecordStoreDbContext> _readOnly;

    public FailableRecordContexts(IDbContextFactory<RecordStoreDbContext> inner, string databasePath)
    {
        _inner = inner;
        _readOnly = new DbContextOptionsBuilder<RecordStoreDbContext>()
            .UseSqlite(RecordStoreConnection.ConnectionString(databasePath, SqliteOpenMode.ReadOnly))
            .AddInterceptors(RecordStorePragmas.Instance)
            .Options;
    }

    public bool Failing { get; set; }

    public bool WritesFailing { get; set; }

    public RecordStoreDbContext CreateDbContext()
    {
        if (Failing)
            throw new SqliteException("disk I/O error (simulated)", SqliteIoError);

        return WritesFailing ? new RecordStoreDbContext(_readOnly) : _inner.CreateDbContext();
    }
}
