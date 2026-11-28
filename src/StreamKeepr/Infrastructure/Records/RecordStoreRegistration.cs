using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Infrastructure.Records;

public static class RecordStoreRegistration
{
    /// <summary>
    /// Registers the context factory over <c>Storage:DatabasePath</c> and this start's
    /// <see cref="RecordStoreState"/>. The host registers <see cref="RecordStoreStartup"/> itself,
    /// right after preflight, because hosted-service order is what makes it run first.
    /// </summary>
    public static IServiceCollection AddStreamKeeprRecordStore(this IServiceCollection services)
    {
        services.AddSingleton<RecordStoreState>();
        services.AddDbContextFactory<RecordStoreDbContext>((provider, options) => options.UseRecordStore(
            provider.GetRequiredService<IOptions<StorageOptions>>().Value.DatabasePath));
        return services;
    }

    /// <summary>
    /// The one place a record-store context is configured: SQLite without create (only
    /// <see cref="RecordStoreStartup"/> creates the file) and the per-connection pragmas.
    /// </summary>
    public static DbContextOptionsBuilder UseRecordStore(this DbContextOptionsBuilder options, string databasePath) =>
        options
            .UseSqlite(RecordStoreConnection.ConnectionString(databasePath, SqliteOpenMode.ReadWrite))
            .AddInterceptors(RecordStorePragmas.Instance);
}
