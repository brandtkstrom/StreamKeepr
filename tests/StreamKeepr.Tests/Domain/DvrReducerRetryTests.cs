using StreamKeepr.Domain;

namespace StreamKeepr.Tests.Domain;

public sealed class DvrReducerRetryTests
{
    [Theory]
    [InlineData(RecorderExitKind.Exited, true, 60, 0)]
    [InlineData(RecorderExitKind.Exited, true, 61, 0)]
    [InlineData(RecorderExitKind.Exited, true, 59, 2)]
    [InlineData(RecorderExitKind.Killed, true, 120, 2)]
    [InlineData(RecorderExitKind.Exited, false, 0, 2)]
    public void Exit_classification_uses_kind_media_and_the_healthy_part_threshold(
        RecorderExitKind kind, bool hasMedia, int elapsedSeconds, int expectedStreak)
    {
        var firstMediaAt = DomainTestData.StartedAt;
        var recording = DomainTestData.Recording(
            attempt: 0,
            hasMedia: hasMedia,
            streak: 1,
            firstMediaAt: hasMedia ? firstMediaAt : null);
        var exit = DomainTestData.RecorderExited(
            recording, firstMediaAt.AddSeconds(elapsedSeconds), kind, "network reset");

        var step = DvrReducer.Transition(recording, exit, DomainTestData.Settings);

        var waiting = Assert.IsType<DvrState.WaitingToRetry>(step.Next);
        Assert.Equal(expectedStreak, waiting.Streak);
        Assert.Equal(1, waiting.Attempt);
        Assert.Equal(recording.Session, waiting.Session);
        Assert.Equal(recording.Latest, waiting.Latest);
        Assert.Equal(new JobId(recording.Session.Id, 2), waiting.Next);

        var schedule = Assert.IsType<DvrCommand.ScheduleRetry>(Assert.Single(step.Commands));
        Assert.Equal(waiting.Next, schedule.Job);
        Assert.Equal(DomainTestData.Settings.RetryBase, schedule.BaseDelay);
    }

    [Fact]
    public void Exit_without_media_increments_the_attempt()
    {
        var recording = DomainTestData.Recording(attempt: 2, streak: 1);

        var step = DvrReducer.Transition(
            recording,
            DomainTestData.RecorderExited(recording, DomainTestData.StartedAt.AddMinutes(1)),
            DomainTestData.Settings);

        var waiting = Assert.IsType<DvrState.WaitingToRetry>(step.Next);
        Assert.Equal(3, waiting.Attempt);
        Assert.Equal(2, waiting.Streak);
    }

    [Fact]
    public void A_part_that_produced_media_restarts_the_attempt_at_one()
    {
        var firstMediaAt = DomainTestData.StartedAt;
        var recording = DomainTestData.Recording(
            attempt: 5, hasMedia: true, streak: 1, firstMediaAt: firstMediaAt);

        var step = DvrReducer.Transition(
            recording,
            DomainTestData.RecorderExited(
                recording, firstMediaAt + DomainTestData.Settings.HealthyPart),
            DomainTestData.Settings);

        var waiting = Assert.IsType<DvrState.WaitingToRetry>(step.Next);
        Assert.Equal(1, waiting.Attempt);
        Assert.Equal(0, waiting.Streak);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void The_max_retries_bound_is_inclusive_and_exhaustion_leaves_a_resumable_idle(
        int attempt)
    {
        var recording = DomainTestData.Recording(attempt: attempt, streak: 1);

        var step = DvrReducer.Transition(
            recording,
            DomainTestData.RecorderExited(recording, DomainTestData.StartedAt.AddMinutes(1)),
            DomainTestData.Settings);

        if (attempt < DomainTestData.Settings.MaxRetries)
        {
            var waiting = Assert.IsType<DvrState.WaitingToRetry>(step.Next);
            Assert.Equal(attempt + 1, waiting.Attempt);
            Assert.IsType<DvrCommand.ScheduleRetry>(Assert.Single(step.Commands));
            return;
        }

        var idle = Assert.IsType<DvrState.Idle>(step.Next);
        Assert.Equal(new Resumable(recording.Session, 2), idle.Resume);
        Assert.Null(idle.Finished);
        Assert.Equal(2, idle.Streak);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Exhausted_retries_leave_idle_with_a_resumable_and_no_commands()
    {
        var recording = DomainTestData.Recording(
            attempt: DomainTestData.Settings.MaxRetries, streak: 1);

        var step = DvrReducer.Transition(
            recording,
            DomainTestData.RecorderExited(recording, DomainTestData.StartedAt.AddMinutes(1)),
            DomainTestData.Settings);

        var idle = Assert.IsType<DvrState.Idle>(step.Next);
        Assert.Equal(new Resumable(recording.Session, 2), idle.Resume);
        Assert.Null(idle.Finished);
        Assert.Equal(2, idle.Streak);
        Assert.Empty(step.Commands);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(7, 60)]
    public void Backoff_doubles_from_retry_base_and_is_capped_by_retry_max(
        int attempt, int expectedSeconds)
    {
        var settings = DomainTestData.Settings with { MaxRetries = 10 };
        var recording = DomainTestData.Recording(attempt: attempt - 1);

        var step = DvrReducer.Transition(
            recording,
            DomainTestData.RecorderExited(recording, DomainTestData.StartedAt.AddMinutes(1)),
            settings);

        var waiting = Assert.IsType<DvrState.WaitingToRetry>(step.Next);
        Assert.Equal(attempt, waiting.Attempt);
        var schedule = Assert.IsType<DvrCommand.ScheduleRetry>(Assert.Single(step.Commands));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), schedule.BaseDelay);
    }

    [Fact]
    public void Backoff_caps_a_base_delay_above_retry_max_on_the_first_attempt()
    {
        var settings = DomainTestData.Settings with
        {
            RetryBase = TimeSpan.FromMinutes(2),
            RetryMax = TimeSpan.FromMinutes(1),
        };
        var recording = DomainTestData.Recording();

        var step = DvrReducer.Transition(
            recording,
            DomainTestData.RecorderExited(recording, DomainTestData.StartedAt.AddMinutes(1)),
            settings);

        var schedule = Assert.IsType<DvrCommand.ScheduleRetry>(Assert.Single(step.Commands));
        Assert.Equal(TimeSpan.FromMinutes(1), schedule.BaseDelay);
    }

    [Fact]
    public void Retry_timer_starts_the_next_part_for_the_matching_job()
    {
        var waiting = DomainTestData.WaitingToRetry(
            attempt: 3, streak: 2, nextPart: 4);

        var step = DvrReducer.Transition(
            waiting, DomainTestData.RetryElapsed(waiting.Next), DomainTestData.Settings);

        var recording = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(waiting.Session, recording.Session);
        Assert.Equal(waiting.Next, recording.Job);
        Assert.Equal(waiting.Latest, recording.Latest);
        Assert.Equal(3, recording.Attempt);
        Assert.Equal(2, recording.Streak);
        Assert.False(recording.HasMedia);
        Assert.Null(recording.FirstMediaAt);

        var start = Assert.IsType<DvrCommand.StartRecorder>(Assert.Single(step.Commands));
        Assert.Equal(waiting.Session, start.Session);
        Assert.Equal(waiting.Next, start.Job);
    }

    [Fact]
    public void Retry_timer_after_an_offline_is_skipped()
    {
        var waiting = DomainTestData.WaitingToRetry(offlineCount: 1);

        var step = DvrReducer.Transition(
            waiting, DomainTestData.RetryElapsed(waiting.Next), DomainTestData.Settings);

        Assert.Same(waiting, step.Next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Retry_timer_for_a_stale_job_changes_nothing()
    {
        var waiting = DomainTestData.WaitingToRetry(nextPart: 3);
        JobId[] staleJobs =
        [
            new(waiting.Session.Id, waiting.Next.Part + 1),
            new("20261110T170000Z_987654321", waiting.Next.Part),
        ];

        foreach (var job in staleJobs)
        {
            var step = DvrReducer.Transition(
                waiting, DomainTestData.RetryElapsed(job), DomainTestData.Settings);

            Assert.Same(waiting, step.Next);
            Assert.Empty(step.Commands);
        }
    }

    [Fact]
    public void Retry_timer_outside_waiting_to_retry_changes_nothing()
    {
        var recording = DomainTestData.Recording();
        var resume = new Resumable(recording.Session, 2);
        DvrState[] states =
        [
            new DvrState.Idle(),
            recording,
            new DvrState.CoolingDown(DomainTestData.StartedAt.AddMinutes(15), resume, "failure"),
            new DvrState.Paused(resume),
        ];
        var timer = DomainTestData.RetryElapsed(recording.Job);

        foreach (var state in states)
        {
            var step = DvrReducer.Transition(state, timer, DomainTestData.Settings);

            Assert.Same(state, step.Next);
            Assert.Empty(step.Commands);
        }
    }

    [Fact]
    public void The_first_offline_while_waiting_counts_and_does_not_finalize()
    {
        var waiting = DomainTestData.WaitingToRetry(offlineCount: 0);

        var step = DvrReducer.Transition(
            waiting, DomainTestData.ObservedOffline(), DomainTestData.Settings);

        var next = Assert.IsType<DvrState.WaitingToRetry>(step.Next);
        Assert.Equal(waiting with { OfflineCount = 1 }, next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Two_consecutive_offlines_finalize_the_session_and_leave_idle()
    {
        var waiting = DomainTestData.WaitingToRetry(streak: 2, nextPart: 4, offlineCount: 1);

        var step = DvrReducer.Transition(
            waiting, DomainTestData.ObservedOffline(), DomainTestData.Settings);

        var idle = Assert.IsType<DvrState.Idle>(step.Next);
        Assert.Null(idle.Resume);
        Assert.Equal(new Resumable(waiting.Session, 4), idle.Finished);
        Assert.Equal(0, idle.Streak);

        var finalize = Assert.IsType<DvrCommand.FinalizeSession>(Assert.Single(step.Commands));
        Assert.Equal(waiting.Session, finalize.Session);
    }

    [Fact]
    public void A_live_observation_between_offlines_starts_the_count_over()
    {
        var waiting = DomainTestData.WaitingToRetry(offlineCount: 0);

        var afterFirstOffline = DvrReducer.Transition(
            waiting, DomainTestData.ObservedOffline(), DomainTestData.Settings);
        var offline = Assert.IsType<DvrState.WaitingToRetry>(afterFirstOffline.Next);

        var afterLive = DvrReducer.Transition(
            offline, DomainTestData.ObservedLive(offline.Latest), DomainTestData.Settings);
        var reset = Assert.IsType<DvrState.WaitingToRetry>(afterLive.Next);
        Assert.Equal(0, reset.OfflineCount);

        var afterSecondOffline = DvrReducer.Transition(
            reset, DomainTestData.ObservedOffline(), DomainTestData.Settings);
        var countedAgain = Assert.IsType<DvrState.WaitingToRetry>(afterSecondOffline.Next);
        Assert.Equal(1, countedAgain.OfflineCount);
    }

    [Fact]
    public void Unknown_between_offlines_does_not_reset_the_count()
    {
        var waiting = DomainTestData.WaitingToRetry(offlineCount: 1);
        var unknown = DomainTestData.Observed(
            new StreamObservation.Unknown("Helix failed"), DomainTestData.StartedAt);

        var afterUnknown = DvrReducer.Transition(waiting, unknown, DomainTestData.Settings);
        Assert.Same(waiting, afterUnknown.Next);

        var afterOffline = DvrReducer.Transition(
            waiting, DomainTestData.ObservedOffline(), DomainTestData.Settings);
        Assert.IsType<DvrState.Idle>(afterOffline.Next);
    }

    [Fact]
    public void Live_same_stream_while_waiting_resets_the_offline_count_without_metadata_change()
    {
        var waiting = DomainTestData.WaitingToRetry(offlineCount: 1);

        var step = DvrReducer.Transition(
            waiting,
            DomainTestData.ObservedLive(waiting.Latest),
            DomainTestData.Settings);

        var next = Assert.IsType<DvrState.WaitingToRetry>(step.Next);
        Assert.Equal(waiting with { OfflineCount = 0 }, next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Live_same_stream_while_waiting_records_metadata_and_resets_the_offline_count()
    {
        var waiting = DomainTestData.WaitingToRetry(offlineCount: 1);
        var changed = waiting.Latest with { Title = "A new title" };

        var step = DvrReducer.Transition(
            waiting, DomainTestData.ObservedLive(changed), DomainTestData.Settings);

        var next = Assert.IsType<DvrState.WaitingToRetry>(step.Next);
        Assert.Equal(changed, next.Latest);
        Assert.Equal(0, next.OfflineCount);
        Assert.Equal(waiting.Streak, next.Streak);

        var metadata = Assert.IsType<DvrCommand.RecordMetadata>(Assert.Single(step.Commands));
        Assert.Equal(waiting.Session, metadata.Session);
        Assert.Equal(changed, metadata.Stream);
    }

    [Fact]
    public void A_different_stream_while_waiting_finalizes_opens_and_starts_in_order()
    {
        var waiting = DomainTestData.WaitingToRetry(streak: 2, nextPart: 3);
        var replacement = DomainTestData.Live(
            streamId: "987654321",
            title: "Second stream",
            game: "Another game",
            startedAt: DomainTestData.StartedAt.AddMinutes(10));

        var step = DvrReducer.Transition(
            waiting, DomainTestData.ObservedLive(replacement), DomainTestData.Settings);

        var recording = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(replacement, recording.Latest);
        Assert.Equal("20261110T164000Z_987654321", recording.Session.Id);
        Assert.Equal(new JobId(recording.Session.Id, 1), recording.Job);
        Assert.Equal(0, recording.Attempt);
        Assert.Equal(2, recording.Streak);
        Assert.False(recording.HasMedia);

        Assert.Collection(
            step.Commands,
            command => Assert.Equal(new DvrCommand.FinalizeSession(waiting.Session), command),
            command => Assert.Equal(new DvrCommand.OpenSession(recording.Session, replacement), command),
            command => Assert.Equal(new DvrCommand.StartRecorder(recording.Session, recording.Job), command));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reaching_failures_before_cooldown_enters_cooling_down(bool hasMedia)
    {
        var firstMediaAt = DomainTestData.StartedAt;
        var recording = DomainTestData.Recording(
            attempt: 0,
            hasMedia: hasMedia,
            streak: DomainTestData.Settings.FailuresBeforeCooldown - 1,
            firstMediaAt: hasMedia ? firstMediaAt : null);
        var exitAt = firstMediaAt.AddSeconds(30);
        var exit = DomainTestData.RecorderExited(recording, exitAt, detail: "streamlink died");

        var step = DvrReducer.Transition(recording, exit, DomainTestData.Settings);

        var cooling = Assert.IsType<DvrState.CoolingDown>(step.Next);
        Assert.Equal(exitAt + DomainTestData.Settings.Cooldown, cooling.Until);
        Assert.Equal(new Resumable(recording.Session, 2), cooling.Resume);
        Assert.Equal("streamlink died", cooling.LastError);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void A_healthy_part_resets_the_streak_instead_of_entering_cooldown()
    {
        var firstMediaAt = DomainTestData.StartedAt;
        var recording = DomainTestData.Recording(
            hasMedia: true,
            streak: DomainTestData.Settings.FailuresBeforeCooldown - 1,
            firstMediaAt: firstMediaAt);

        var step = DvrReducer.Transition(
            recording,
            DomainTestData.RecorderExited(
                recording, firstMediaAt + DomainTestData.Settings.HealthyPart),
            DomainTestData.Settings);

        var waiting = Assert.IsType<DvrState.WaitingToRetry>(step.Next);
        Assert.Equal(0, waiting.Streak);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cooling_down_ignores_live_and_offline_before_until(bool live)
    {
        var liveStream = DomainTestData.Live();
        var resume = new Resumable(DomainTestData.Recording(live: liveStream).Session, 2);
        var until = DomainTestData.StartedAt.AddMinutes(15);
        var cooling = new DvrState.CoolingDown(until, resume, "too many failures");
        var before = until.AddSeconds(-1);
        var observation = live
            ? DomainTestData.ObservedLive(liveStream, before)
            : DomainTestData.ObservedOffline(before);

        var step = DvrReducer.Transition(cooling, observation, DomainTestData.Settings);

        Assert.Same(cooling, step.Next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Cooling_down_ignores_unknown_after_until()
    {
        var resume = new Resumable(DomainTestData.Recording().Session, 2);
        var until = DomainTestData.StartedAt.AddMinutes(15);
        var cooling = new DvrState.CoolingDown(until, resume, "too many failures");
        var unknown = DomainTestData.Observed(
            new StreamObservation.Unknown("Helix failed"), until.AddMinutes(1));

        var step = DvrReducer.Transition(cooling, unknown, DomainTestData.Settings);

        Assert.Same(cooling, step.Next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Cooling_down_expiry_delegates_to_idle_and_keeps_a_resumable()
    {
        var live = DomainTestData.Live();
        var resume = new Resumable(DomainTestData.Recording(live: live).Session, 2);
        var until = DomainTestData.StartedAt.AddMinutes(15);
        var cooling = new DvrState.CoolingDown(until, resume, "too many failures");

        var step = DvrReducer.Transition(
            cooling, DomainTestData.ObservedLive(live, until), DomainTestData.Settings);

        var idle = Assert.IsType<DvrState.Idle>(step.Next);
        Assert.Equal(resume, idle.Resume);
        Assert.Null(idle.Finished);
        Assert.Equal(0, idle.Streak);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Cooling_down_expiry_without_a_resumable_starts_a_new_recording()
    {
        var live = DomainTestData.Live();
        var until = DomainTestData.StartedAt.AddMinutes(15);
        var cooling = new DvrState.CoolingDown(until, Resume: null, "too many failures");

        var step = DvrReducer.Transition(
            cooling, DomainTestData.ObservedLive(live, until), DomainTestData.Settings);

        var recording = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(new JobId(recording.Session.Id, 1), recording.Job);
        Assert.Equal(0, recording.Streak);
        Assert.Collection(
            step.Commands,
            command => Assert.IsType<DvrCommand.OpenSession>(command),
            command => Assert.IsType<DvrCommand.StartRecorder>(command));
    }

    [Fact]
    public void One_segment_then_die_reaches_cooling_down_after_the_configured_failures()
    {
        var settings = DomainTestData.Settings;
        var recording = DomainTestData.Recording();
        var at = recording.Latest.StartedAt;

        for (var part = 1; part <= settings.FailuresBeforeCooldown; part++)
        {
            var mediaAt = at.AddSeconds(10);
            var media = DvrReducer.Transition(
                recording,
                new DvrEvent.PartProducingMedia(recording.Job, mediaAt),
                settings);
            recording = Assert.IsType<DvrState.Recording>(media.Next);
            Assert.Equal(part, recording.Job.Part);
            Assert.Equal(0, recording.Attempt);

            at = mediaAt.AddSeconds(30);
            var exit = DvrReducer.Transition(
                recording,
                DomainTestData.RecorderExited(recording, at),
                settings);

            if (part < settings.FailuresBeforeCooldown)
            {
                var waiting = Assert.IsType<DvrState.WaitingToRetry>(exit.Next);
                Assert.Equal(part, waiting.Streak);
                Assert.Equal(part + 1, waiting.Next.Part);
                Assert.IsType<DvrCommand.ScheduleRetry>(Assert.Single(exit.Commands));

                var timer = DvrReducer.Transition(
                    waiting, DomainTestData.RetryElapsed(waiting.Next), settings);
                recording = Assert.IsType<DvrState.Recording>(timer.Next);
                Assert.Equal(part, recording.Streak);
                Assert.Equal(part + 1, recording.Job.Part);
                continue;
            }

            var cooling = Assert.IsType<DvrState.CoolingDown>(exit.Next);
            Assert.Equal(at + settings.Cooldown, cooling.Until);
            Assert.Equal(
                new Resumable(recording.Session, part + 1), cooling.Resume);
            Assert.Equal("recorder stopped", cooling.LastError);
            Assert.Empty(exit.Commands);
        }
    }

    [Fact]
    public void Stopped_while_recording_is_ignored()
    {
        var recording = DomainTestData.Recording(
            attempt: 2,
            hasMedia: true,
            firstMediaAt: DomainTestData.StartedAt);

        var step = DvrReducer.Transition(
            recording,
            DomainTestData.RecorderExited(
                recording,
                DomainTestData.StartedAt.AddMinutes(10),
                RecorderExitKind.Stopped),
            DomainTestData.Settings);

        Assert.Same(recording, step.Next);
        Assert.Empty(step.Commands);
    }

    [Theory]
    [InlineData(RecorderExitKind.Exited)]
    [InlineData(RecorderExitKind.Killed)]
    public void Recorder_exit_for_a_stale_job_changes_nothing(RecorderExitKind kind)
    {
        var recording = DomainTestData.Recording(part: 2);
        var stale = new DvrEvent.RecorderExited(
            new JobId(recording.Session.Id, 1),
            DomainTestData.StartedAt.AddMinutes(1),
            kind,
            "late event");

        var step = DvrReducer.Transition(recording, stale, DomainTestData.Settings);

        Assert.Same(recording, step.Next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Idle_streak_carries_into_a_new_recording()
    {
        var idle = new DvrState.Idle(Streak: 2);
        var live = DomainTestData.Live();

        var step = DvrReducer.Transition(
            idle, DomainTestData.ObservedLive(live), DomainTestData.Settings);

        var recording = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(2, recording.Streak);
    }

    [Fact]
    public void Recording_streak_carries_when_the_stream_id_changes()
    {
        var recording = DomainTestData.Recording(streak: 2);
        var replacement = DomainTestData.Live(
            streamId: "987654321", startedAt: DomainTestData.StartedAt.AddMinutes(10));

        var step = DvrReducer.Transition(
            recording, DomainTestData.ObservedLive(replacement), DomainTestData.Settings);

        var next = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(2, next.Streak);
    }
}
