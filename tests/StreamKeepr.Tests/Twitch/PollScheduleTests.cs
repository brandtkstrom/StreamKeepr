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

    [Theory]
    [InlineData(0, 0, PushState.PollingOnly)]
    [InlineData(0, 2, PushState.PartialCoverage)]
    [InlineData(1, 2, PushState.PartialCoverage)]
    [InlineData(2, 2, PushState.Connected)]
    public void Coverage_is_all_or_nothing(int covered, int watched, PushState expected) =>
        Assert.Equal(expected, PushStatus.FromCoverage(Logins(covered), watched).State);

    [Fact]
    public void Coverage_rejects_more_covered_than_watched() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PushStatus.FromCoverage(Logins(3), 2));

    [Fact]
    public void Coverage_carries_which_channels_are_covered()
    {
        PushStatus status = PushStatus.FromCoverage(["ann", "bob"], watched: 3);

        Assert.Equal(2, status.Covered);
        Assert.Equal(3, status.Watched);
        Assert.True(status.Covers("ann"));
        Assert.False(status.Covers("cat"));
        Assert.False(PushStatus.Reconnecting.Covers("ann"));
    }

    [Fact]
    public void Push_statuses_compare_by_state_counts_and_covered_channels()
    {
        Assert.Equal(PushStatus.FromCoverage(["ann", "bob"], 2), PushStatus.FromCoverage(["bob", "ann"], 2));
        Assert.Equal(
            PushStatus.FromCoverage(["ann"], 2).GetHashCode(),
            PushStatus.FromCoverage(["ann"], 2).GetHashCode());
        Assert.NotEqual(PushStatus.FromCoverage(["ann"], 2), PushStatus.FromCoverage(["bob"], 2));
        Assert.NotEqual(PushStatus.FromCoverage(["ann"], 1), PushStatus.AllConnected);
        Assert.NotEqual(
            PushStatus.FromCoverage([], 2, sessionOpen: false), PushStatus.FromCoverage([], 2));
    }

    [Fact]
    public void Coverage_without_an_open_socket_has_no_session()
    {
        Assert.True(PushStatus.FromCoverage([], 2).HasSession);
        Assert.False(PushStatus.FromCoverage([], 2, sessionOpen: false).HasSession);
        Assert.Equal(
            PushState.PartialCoverage, PushStatus.FromCoverage([], 2, sessionOpen: false).State);
        Assert.False(PushStatus.Reconnecting.HasSession);
    }

    [Fact]
    public void Liveness_timestamps_are_recorded_without_raising_changed()
    {
        var clock = NewClock();
        var schedule = new PollSchedule(clock);
        int changes = 0;
        schedule.Changed += () => changes++;

        Assert.Null(schedule.LastEventSubMessageAt);
        Assert.Null(schedule.LastSuccessfulPollAt);
        Assert.Null(schedule.LastApiRoundTrip);

        clock.Advance(TimeSpan.FromSeconds(5));
        schedule.EventSubMessageReceived();
        Assert.Equal(clock.GetUtcNow(), schedule.LastEventSubMessageAt);

        clock.Advance(TimeSpan.FromSeconds(5));
        schedule.Polled(succeeded: false);
        Assert.Equal(clock.GetUtcNow(), schedule.LastPollAt);
        Assert.Null(schedule.LastSuccessfulPollAt);

        clock.Advance(TimeSpan.FromSeconds(5));
        schedule.Polled(succeeded: true, roundTrip: TimeSpan.FromMilliseconds(180));
        Assert.Equal(schedule.LastPollAt, schedule.LastSuccessfulPollAt);
        Assert.Equal(TimeSpan.FromMilliseconds(180), schedule.LastApiRoundTrip);

        // Nothing to ask Helix: a success that keeps the previous round trip.
        clock.Advance(TimeSpan.FromSeconds(5));
        schedule.Polled();
        Assert.Equal(clock.GetUtcNow(), schedule.LastSuccessfulPollAt);
        Assert.Equal(TimeSpan.FromMilliseconds(180), schedule.LastApiRoundTrip);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_negative_round_trip_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PollSchedule(NewClock()).Polled(roundTrip: TimeSpan.FromMilliseconds(-1)));

    [Fact]
    public async Task Current_interval_reports_the_cadence_the_last_wait_chose()
    {
        var clock = NewClock();
        var schedule = new PollSchedule(clock);
        Assert.Null(schedule.CurrentInterval);

        Task wait = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        Assert.Equal(Normal, schedule.CurrentInterval);
        schedule.Nudge();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        schedule.SetPushStatus(PushStatus.FromCoverage(["ann"], 1));
        wait = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        Assert.Equal(WithPush, schedule.CurrentInterval);
        schedule.Nudge();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        schedule.Burst(TimeSpan.FromMinutes(3));
        await Task.Yield();
        wait = schedule.WaitAsync(Normal, WithPush, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.CurrentInterval);
        schedule.Nudge();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static string[] Logins(int count) =>
        Enumerable.Range(0, count).Select(i => $"channel{i}").ToArray();

    private static FakeTimeProvider NewClock() =>
        new(new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));
}
