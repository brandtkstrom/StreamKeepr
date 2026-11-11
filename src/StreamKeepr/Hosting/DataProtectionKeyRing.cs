using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Hosting;

/// <summary>
/// Registers Data Protection with a key ring persisted on the recordings volume,
/// alongside the admin token store and outside the media surface. The repository
/// is configured through <see cref="KeyManagementOptions"/> rather than
/// PersistKeysToFileSystem so its path comes from validated storage options and
/// key files can be restricted to the owner.
/// </summary>
public static class DataProtectionKeyRing
{
    public const string ApplicationName = "StreamKeepr";

    public static IServiceCollection AddStreamKeeprDataProtection(this IServiceCollection services)
    {
        services.AddDataProtection()
            .SetApplicationName(ApplicationName);

        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(serviceProvider =>
        {
            // StorageOptions is bound and validated by the host before this options
            // instance is resolved, so a missing root still reports the option error.
            var recordingsRoot = serviceProvider.GetRequiredService<IOptions<StorageOptions>>()
                .Value.RecordingsRoot;
            var keyDirectory = AppDataPaths.KeysDirectory(recordingsRoot);
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

            // Data Protection is resolved while the request pipeline is built, which
            // happens before hosted services start. Defer the directory probe so
            // ToolPreflight always reports an unwritable volume first.
            var repository = new Lazy<IDeletableXmlRepository>(() =>
            {
                OwnerOnlyFileSystem.EnsureWritableKeyDirectory(keyDirectory);
                return new OwnerOnlyXmlRepository(
                    new FileSystemXmlRepository(new DirectoryInfo(keyDirectory), loggerFactory),
                    keyDirectory,
                    loggerFactory.CreateLogger<OwnerOnlyXmlRepository>());
            });

            return new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlRepository = new LazyXmlRepository(repository));
        });

        services.AddHostedService<DataProtectionKeyRingStartupCheck>();
        return services;
    }
}
