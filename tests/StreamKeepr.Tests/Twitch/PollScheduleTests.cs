using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

public sealed class PollScheduleTests
{
    private static readonly TimeSpan Normal = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WithPush = TimeSpan.FromSeconds(300);

    [Fact]
    public async Task Uses_the_normal_interval_without_full_push_coverage()
    {
        var clock = NewClock();
        var schedule = new PollSchedule(clock);

        Task wait = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(wait.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Uses_the_push_interval_only_when_every_channel_is_covered()
    {
        var clock = NewClock();
        var schedule = new PollSchedule(clock);
        schedule.SetPushStatus(PushStatus.AllConnected);

        Task wait = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(299));
        Assert.False(wait.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Burst_uses_ten_seconds_for_three_minutes()
    {
        var clock = NewClock();
        var schedule = new PollSchedule(clock);

        Task wake = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        schedule.Burst(TimeSpan.FromMinutes(3));
        await wake.WaitAsync(TimeSpan.FromSeconds(1));

        Task first = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(first.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await first.WaitAsync(TimeSpan.FromSeconds(1));

        Task second = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        await second.WaitAsync(TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(180));
        Task afterBurst = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(299));
        Assert.False(afterBurst.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await afterBurst.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Nudge_wakes_the_wait_and_coalesces()
    {
        var clock = NewClock();
        var schedule = new PollSchedule(clock);

        Task wait = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        schedule.Nudge();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        schedule.Nudge();
        schedule.Nudge();
        await schedule.WaitAsync(Normal, WithPush, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Task next = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        Assert.False(next.IsCompleted);
        clock.Advance(Normal);
        await next.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Losing_push_coverage_wakes_a_pending_wait()
    {
        var clock = NewClock();
        var schedule = new PollSchedule(clock);
        schedule.SetPushStatus(PushStatus.AllConnected);

        Task wait = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        schedule.SetPushStatus(PushStatus.PollingOnly);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        Task normal = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(normal.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await normal.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void LastPollAt_updates_on_polled()
    {
        var clock = NewClock();
        var schedule = new PollSchedule(clock);
        DateTimeOffset start = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromSeconds(5));
        schedule.Polled();

        Assert.Equal(start.AddSeconds(5), schedule.LastPollAt);
        Assert.False(schedule.IsPushConnected);
    }

    [Fact]
    public void Push_status_reports_the_contract_reason_strings()
    {
        Assert.Equal("Polling only", PushStatus.PollingOnly.Reason);
        Assert.Equal("No admin token", PushStatus.NoAdminToken.Reason);
        Assert.Equal("EventSub reconnecting", PushStatus.Reconnecting.Reason);
        Assert.Equal("Partial coverage", PushStatus.PartialCoverage.Reason);
        Assert.Equal("EventSub connected", PushStatus.AllConnected.Reason);
        Assert.True(PushStatus.AllConnected.Connected);
        Assert.False(PushStatus.PartialCoverage.Connected);
    }

    private static FakeTimeProvider NewClock() =>
        new(new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
}
