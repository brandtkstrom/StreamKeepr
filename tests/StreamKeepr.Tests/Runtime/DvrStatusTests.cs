using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;

namespace StreamKeepr.Tests.Runtime;

public sealed class DvrStatusTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Current_is_a_copy_that_later_updates_do_not_mutate()
    {
        var status = new DvrStatus();
        status.Applied("streamer", null, new DvrState.Idle(), T0);

        IReadOnlyDictionary<string, DvrSnapshot> snapshot = status.Current;

        status.Applied("streamer", null, new DvrState.Paused(), T0.AddSeconds(1));

        Assert.IsType<DvrState.Idle>(snapshot["streamer"].State);
        Assert.IsType<DvrState.Paused>(status.Current["streamer"].State);
    }

    [Fact]
    public void A_new_active_job_resets_part_telemetry()
    {
        var status = new DvrStatus();
        var first = new JobId("session", 1);
        var second = new JobId("session", 2);

        status.Applied("streamer", null, Recording(first), T0);
        status.ReportProgress("streamer", first, 4096, TimeSpan.FromSeconds(4), T0);

        Assert.Equal(4096, status.Current["streamer"].PartBytes);
        Assert.Equal(TimeSpan.FromSeconds(4), status.Current["streamer"].PartMediaTime);

        status.Applied("streamer", null, Recording(second), T0.AddSeconds(1));

        DvrSnapshot snapshot = status.Current["streamer"];
        Assert.Equal(second, snapshot.ActiveJob);
        Assert.Equal(0, snapshot.PartBytes);
        Assert.Equal(TimeSpan.Zero, snapshot.PartMediaTime);
        Assert.Null(snapshot.LastProgressAt);
    }

    [Fact]
    public void Progress_for_a_stale_job_is_ignored_and_does_not_notify()
    {
        var status = new DvrStatus();
        var active = new JobId("session", 1);
        var stale = new JobId("session", 2);
        status.Applied("streamer", null, Recording(active), T0);

        int changed = 0;
        status.Changed += () => changed++;

        status.ReportProgress("streamer", stale, 8192, TimeSpan.FromSeconds(8), T0);

        Assert.Equal(0, changed);
        Assert.Equal(0, status.Current["streamer"].PartBytes);

        status.ReportProgress("streamer", active, 8192, TimeSpan.FromSeconds(8), T0);

        Assert.Equal(1, changed);
        Assert.Equal(8192, status.Current["streamer"].PartBytes);
    }

    [Fact]
    public void Exit_details_are_recorded_while_errors_survive_normal_exits()
    {
        var status = new DvrStatus();
        var job = new JobId("session", 1);
        status.Applied("streamer", null, Recording(job), T0);

        status.Applied(
            "streamer",
            new DvrEvent.RecorderExited(job, T0, RecorderExitKind.Exited, "stream ended"),
            new DvrState.Idle(),
            T0);

        Assert.Equal("stream ended", status.Current["streamer"].LastDetail);
        Assert.Null(status.Current["streamer"].LastError);

        status.Failed("streamer", "finalize failed");
        Assert.Equal("finalize failed", status.Current["streamer"].LastError);

        status.Applied(
            "streamer",
            new DvrEvent.RecorderExited(job, T0, RecorderExitKind.Exited, "later exit"),
            new DvrState.Idle(),
            T0);

        Assert.Equal("later exit", status.Current["streamer"].LastDetail);
        Assert.Equal("finalize failed", status.Current["streamer"].LastError);

        status.Applied(
            "streamer",
            new DvrEvent.RecorderExited(job, T0, RecorderExitKind.Killed, "watchdog"),
            new DvrState.Idle(),
            T0);

        Assert.Equal("watchdog", status.Current["streamer"].LastError);
    }

    [Fact]
    public void Applied_records_the_last_observation_and_remove_drops_the_snapshot()
    {
        var status = new DvrStatus();
        var observation = new StreamObservation.Live(
            new LiveStream("123", "Title", "Game", T0, null));

        status.Applied(
            "streamer",
            new DvrEvent.StreamStatusObserved(observation, T0),
            new DvrState.Idle(),
            T0);

        Assert.Same(observation, status.Current["streamer"].LastObservation);

        int changed = 0;
        status.Changed += () => changed++;

        status.Remove("streamer");

        Assert.Equal(1, changed);
        Assert.Empty(status.Current);

        status.Remove("missing");
        Assert.Equal(1, changed);
    }

    private static DvrState.Recording Recording(JobId job) =>
        new(
            new Session(
                "20261110T163000Z_123",
                "streamer",
                "123",
                T0,
                Path.Combine(Path.GetTempPath(), "streamkeepr-tests", "session")),
            job,
            new LiveStream("123", "Title", "Game", T0, null),
            Attempt: 0,
            HasMedia: false,
            Streak: 0);
}
