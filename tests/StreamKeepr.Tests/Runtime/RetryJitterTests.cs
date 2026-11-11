using StreamKeepr.Infrastructure.Runtime;

namespace StreamKeepr.Tests.Runtime;

public sealed class RetryJitterTests
{
    [Theory]
    [InlineData(0.0, 0.75)]
    [InlineData(0.25, 0.875)]
    [InlineData(0.5, 1.0)]
    public void Jitter_scales_the_base_delay_within_25_percent(double sample, double factor)
    {
        TimeSpan baseDelay = TimeSpan.FromSeconds(2);

        TimeSpan actual = new RandomizedRetryJitter().Apply(baseDelay, sample);

        Assert.Equal(TimeSpan.FromTicks((long)(baseDelay.Ticks * factor)), actual);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.0)]
    public void Jitter_rejects_samples_outside_the_unit_interval(double sample)
    {
        var jitter = new RandomizedRetryJitter();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => jitter.Apply(TimeSpan.FromSeconds(2), sample));
    }
}
