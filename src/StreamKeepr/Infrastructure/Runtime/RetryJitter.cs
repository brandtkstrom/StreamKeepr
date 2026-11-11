namespace StreamKeepr.Infrastructure.Runtime;

/// <summary>Adds ±25 % jitter to retry delays. Injectable so tests stay deterministic.</summary>
public interface IRetryJitter
{
    TimeSpan Apply(TimeSpan baseDelay);
}

/// <summary>Production jitter using the shared thread-safe random source.</summary>
public sealed class RandomizedRetryJitter : IRetryJitter
{
    public TimeSpan Apply(TimeSpan baseDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseDelay, TimeSpan.Zero);
        return baseDelay * (0.75 + (Random.Shared.NextDouble() * 0.5));
    }
}
