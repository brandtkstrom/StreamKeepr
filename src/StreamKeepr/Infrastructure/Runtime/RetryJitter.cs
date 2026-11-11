namespace StreamKeepr.Infrastructure.Runtime;

/// <summary>Adds ±25 % jitter to retry delays. Injectable so tests stay deterministic.</summary>
public interface IRetryJitter
{
    TimeSpan Apply(TimeSpan baseDelay);
}

/// <summary>Production jitter using the shared thread-safe random source.</summary>
public sealed class RandomizedRetryJitter : IRetryJitter
{
    public TimeSpan Apply(TimeSpan baseDelay) =>
        Apply(baseDelay, Random.Shared.NextDouble());

    /// <summary>
    /// The pure ±25 % calculation, exposed so tests can supply a deterministic sample instead
    /// of asserting on production randomness.
    /// </summary>
    public TimeSpan Apply(TimeSpan baseDelay, double sample)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(sample, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sample, 1.0);

        return baseDelay * (0.75 + (sample * 0.5));
    }
}
