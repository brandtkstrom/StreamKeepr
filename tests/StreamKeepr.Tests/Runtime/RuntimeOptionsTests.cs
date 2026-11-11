using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;

namespace StreamKeepr.Tests.Runtime;

public sealed class RuntimeOptionsTests
{
    [Fact]
    public void Seeded_channels_are_trimmed_lowercased_and_distinct()
    {
        var options = new TwitchOptions
        {
            Channels = [" Streamer ", "streamer", "OTHER"],
        };

        Assert.Equal(["streamer", "other"], options.Channels);
    }

    [Fact]
    public void Dvr_settings_carry_the_recorder_and_storage_thresholds()
    {
        DvrSettings settings = DvrSettingsFactory.Create(
            "streamer",
            new RecorderOptions
            {
                MaxRetries = 4,
                RetryBaseSeconds = 3,
                RetryMaxSeconds = 30,
                FailuresBeforeCooldown = 2,
                CooldownMinutes = 7,
                HealthyPartSeconds = 45,
            },
            new StorageOptions { RecordingsRoot = "/recordings", MinFreeGb = 5 });

        Assert.Equal("/recordings", settings.RecordingsRoot);
        Assert.Equal("streamer", settings.Channel);
        Assert.Equal(4, settings.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(3), settings.RetryBase);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.RetryMax);
        Assert.Equal(2, settings.FailuresBeforeCooldown);
        Assert.Equal(TimeSpan.FromMinutes(7), settings.Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(45), settings.HealthyPart);
    }
}
