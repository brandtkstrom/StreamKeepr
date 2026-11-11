using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StreamKeepr.Hosting;

namespace StreamKeepr.Tests.Hosting;

public sealed class DataProtectionKeyRingTests
{
    private const string Purpose = "StreamKeepr.Tests";

    [Fact]
    public async Task Protected_payload_survives_a_restart_with_the_same_recordings_root()
    {
        using var root = new TempRecordingsRoot();
        string payload;

        using (var first = new StreamKeeprAppFactory(recordingsRoot: root.Path))
        {
            using var client = first.CreateClient();
            payload = CreateProtector(first).Protect("survives a restart");
        }

        using (var second = new StreamKeeprAppFactory(recordingsRoot: root.Path))
        {
            using var client = second.CreateClient();
            using var response = await client.GetAsync("/");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("survives a restart", CreateProtector(second).Unprotect(payload));
        }
    }

    [Fact]
    public void Key_ring_uses_the_private_app_data_folder_with_owner_only_permissions()
    {
        using var root = new TempRecordingsRoot();
        using var factory = new StreamKeeprAppFactory(recordingsRoot: root.Path);
        using var client = factory.CreateClient();

        var keyDirectory = AppDataPaths.KeysDirectory(factory.RecordingsRoot);
        Assert.Equal(
            Path.Combine(factory.RecordingsRoot, ".streamkeepr", "keys"),
            keyDirectory);
        Assert.True(Directory.Exists(keyDirectory));

        var dataProtection = factory.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value;
        Assert.Equal(DataProtectionKeyRing.ApplicationName, dataProtection.ApplicationDiscriminator);

        CreateProtector(factory).Protect("payload");
        var keyFiles = Directory.GetFiles(keyDirectory, "*.xml");
        Assert.NotEmpty(keyFiles);

        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(keyDirectory));
        foreach (var keyFile in keyFiles)
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(keyFile));
        }
    }

    [Fact]
    public async Task Lost_key_ring_is_logged_and_requires_reauthentication_instead_of_crashing()
    {
        using var root = new TempRecordingsRoot();
        string payload;

        using (var first = new StreamKeeprAppFactory(recordingsRoot: root.Path))
        {
            using var client = first.CreateClient();
            payload = CreateProtector(first).Protect("old cookie");
        }

        Directory.Delete(AppDataPaths.KeysDirectory(root.Path), recursive: true);

        using (var second = new StreamKeeprAppFactory(recordingsRoot: root.Path))
        {
            using var client = second.CreateClient();
            using var response = await client.GetAsync("/");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(second.Logs.Messages,
                message => message.Contains("Data Protection key ring", StringComparison.Ordinal)
                    && message.Contains("re-authenticate", StringComparison.Ordinal));
            Assert.Throws<CryptographicException>(() => CreateProtector(second).Unprotect(payload));
        }
    }

    [Fact]
    public async Task Unreadable_key_ring_is_logged_and_requires_reauthentication_instead_of_crashing()
    {
        using var root = new TempRecordingsRoot();
        string payload;

        using (var first = new StreamKeeprAppFactory(recordingsRoot: root.Path))
        {
            using var client = first.CreateClient();
            payload = CreateProtector(first).Protect("old cookie");
        }

        var keyFile = Assert.Single(Directory.GetFiles(AppDataPaths.KeysDirectory(root.Path), "*.xml"));
        await File.WriteAllTextAsync(keyFile, "not xml <<<");

        using (var second = new StreamKeeprAppFactory(recordingsRoot: root.Path))
        {
            using var client = second.CreateClient();
            using var response = await client.GetAsync("/");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(second.Logs.Messages,
                message => message.Contains("Data Protection key ring", StringComparison.Ordinal)
                    && message.Contains("unreadable", StringComparison.Ordinal)
                    && message.Contains("re-authenticate", StringComparison.Ordinal));
            Assert.Throws<CryptographicException>(() => CreateProtector(second).Unprotect(payload));
        }
    }

    [Fact]
    public void Unwritable_key_directory_fails_startup_with_ownership_guidance()
    {
        using var root = new TempRecordingsRoot();
        Directory.CreateDirectory(root.Path);
        var keyDirectory = AppDataPaths.KeysDirectory(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(keyDirectory)!);
        File.WriteAllText(keyDirectory, "blocks the key directory");

        using var factory = new StreamKeeprAppFactory(recordingsRoot: root.Path);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Data Protection key directory", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("1654", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Key_ring_is_not_on_a_media_path()
    {
        // /media only exposes recordings and uses an extension allowlist; the
        // private app-data folder is separate. The HTTP 404 assertion arrives
        // with the media surface in #31.
        var keyDirectory = AppDataPaths.KeysDirectory("/recordings");

        Assert.Equal(Path.Combine("/recordings", ".streamkeepr", "keys"), keyDirectory);
        Assert.StartsWith(
            Path.Combine("/recordings", ".streamkeepr") + Path.DirectorySeparatorChar,
            keyDirectory,
            StringComparison.Ordinal);
    }

    private static IDataProtector CreateProtector(StreamKeeprAppFactory factory) =>
        factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private sealed class TempRecordingsRoot : IDisposable
    {
        public TempRecordingsRoot() =>
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "streamkeepr-tests",
                Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
