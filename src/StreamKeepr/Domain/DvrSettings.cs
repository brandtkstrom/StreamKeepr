namespace StreamKeepr.Domain;

public sealed record DvrSettings(
    string RecordingsRoot,
    string Channel,
    int MaxRetries,
    TimeSpan RetryBase,
    TimeSpan RetryMax,
    int FailuresBeforeCooldown,
    TimeSpan Cooldown,
    TimeSpan HealthyPart);
