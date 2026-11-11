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
            OwnerOnlyFileSystem.EnsureWritableKeyDirectory(keyDirectory);

            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var repository = new FileSystemXmlRepository(new DirectoryInfo(keyDirectory), loggerFactory);
            return new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlRepository = new OwnerOnlyXmlRepository(
                    repository,
                    keyDirectory,
                    loggerFactory.CreateLogger<OwnerOnlyXmlRepository>()));
        });

        services.AddHostedService<DataProtectionKeyRingStartupCheck>();
        return services;
    }
}
