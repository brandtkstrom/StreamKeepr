using StreamKeepr.Configuration;
using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Runtime;

/// <summary>Maps validated options to the pure reducer's per-channel settings.</summary>
public static class DvrSettingsFactory
{
    public static DvrSettings Create(string channel, RecorderOptions recorder, StorageOptions storage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(storage);

        return new DvrSettings(
            storage.RecordingsRoot,
            channel,
            recorder.MaxRetries,
            TimeSpan.FromSeconds(recorder.RetryBaseSeconds),
            TimeSpan.FromSeconds(recorder.RetryMaxSeconds),
            recorder.FailuresBeforeCooldown,
            TimeSpan.FromMinutes(recorder.CooldownMinutes),
            TimeSpan.FromSeconds(recorder.HealthyPartSeconds));
    }
}
