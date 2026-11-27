using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StreamKeepr.Infrastructure.Records;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without starting the app host, which
/// would run preflight and need Twitch credentials. The path is never opened when adding a
/// migration.
/// </summary>
internal sealed class RecordStoreDesignTimeFactory : IDesignTimeDbContextFactory<RecordStoreDbContext>
{
    public RecordStoreDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RecordStoreDbContext>()
            .UseSqlite(RecordStoreConnection.ConnectionString(
                Path.Combine(Path.GetTempPath(), "streamkeepr-design-time.db"),
                SqliteOpenMode.ReadWriteCreate))
            .Options);
}
