using StreamKeepr.Domain;

namespace StreamKeepr.Tests.Domain;

public sealed class DvrReducerLifecycleTests
{
    [Fact]
    public void Stop_from_idle_without_a_resumable_enters_paused()
    {
        var step = DvrReducer.Transition(
            new DvrState.Idle(), new DvrEvent.StopRequested(), DomainTestData.Settings);

        var paused = Assert.IsType<DvrState.Paused>(step.Next);
        Assert.Null(paused.Finished);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Stop_from_idle_finalizes_the_resumable_and_keeps_it_for_resume()
    {
        var resume = DomainTestData.Resumable(nextPart: 3);

        var step = DvrReducer.Transition(
            new DvrState.Idle(Resume: resume, Streak: 2),
            new DvrEvent.StopRequested(),
            DomainTestData.Settings);

        var paused = Assert.IsType<DvrState.Paused>(step.Next);
        Assert.Equal(resume, paused.Finished);
        var finalize = Assert.IsType<DvrCommand.FinalizeSession>(Assert.Single(step.Commands));
        Assert.Equal(resume.Session, finalize.Session);
    }

    [Fact]
    public void Stop_from_idle_without_an_open_session_keeps_the_finished_session()
    {
        var finished = DomainTestData.Resumable(nextPart: 4, complete: true);

        var step = DvrReducer.Transition(
            new DvrState.Idle(Finished: finished),
            new DvrEvent.StopRequested(),
            DomainTestData.Settings);

        var paused = Assert.IsType<DvrState.Paused>(step.Next);
        Assert.Equal(finished, paused.Finished);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Stop_from_idle_with_both_resumables_prefers_finished_and_finalizes_nothing()
    {
        var resume = DomainTestData.Resumable(nextPart: 2);
        var finished = DomainTestData.Resumable(nextPart: 4, complete: true);

        var step = DvrReducer.Transition(
            new DvrState.Idle(Resume: resume, Finished: finished),
            new DvrEvent.StopRequested(),
            DomainTestData.Settings);

        var paused = Assert.IsType<DvrState.Paused>(step.Next);
        Assert.Equal(finished, paused.Finished);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Stop_while_recording_stops_the_job_then_finalizes_and_parks_the_next_part()
    {
        var recording = DomainTestData.Recording(
            part: 2, hasMedia: true, firstMediaAt: DomainTestData.StartedAt);

        var step = DvrReducer.Transition(
            recording, new DvrEvent.StopRequested(), DomainTestData.Settings);

        var paused = Assert.IsType<DvrState.Paused>(step.Next);
        Assert.Equal(new Resumable(recording.Session, 3), paused.Finished);

        Assert.Collection(
            step.Commands,
            command => Assert.Equal(new DvrCommand.StopRecorder(recording.Job), command),
            command => Assert.Equal(new DvrCommand.FinalizeSession(recording.Session), command));
    }

    [Fact]
    public void Stop_while_waiting_to_retry_finalizes_and_parks_the_next_part()
    {
        var waiting = DomainTestData.WaitingToRetry(
            attempt: 2, streak: 2, nextPart: 4, offlineCount: 1);

        var step = DvrReducer.Transition(
            waiting, new DvrEvent.StopRequested(), DomainTestData.Settings);

        var paused = Assert.IsType<DvrState.Paused>(step.Next);
        Assert.Equal(new Resumable(waiting.Session, 4), paused.Finished);

        var finalize = Assert.IsType<DvrCommand.FinalizeSession>(Assert.Single(step.Commands));
        Assert.Equal(waiting.Session, finalize.Session);
    }

    [Fact]
    public void Stop_while_cooling_down_finalizes_the_resumable()
    {
        var resume = DomainTestData.Resumable(nextPart: 4);
        var cooling = new DvrState.CoolingDown(
            DomainTestData.StartedAt.AddMinutes(15), resume, "too many failures");

        var step = DvrReducer.Transition(
            cooling, new DvrEvent.StopRequested(), DomainTestData.Settings);

        var paused = Assert.IsType<DvrState.Paused>(step.Next);
        Assert.Equal(resume, paused.Finished);

        var finalize = Assert.IsType<DvrCommand.FinalizeSession>(Assert.Single(step.Commands));
        Assert.Equal(resume.Session, finalize.Session);
    }

    [Fact]
    public void Stop_while_cooling_down_without_a_resumable_enters_paused()
    {
        var cooling = new DvrState.CoolingDown(
            DomainTestData.StartedAt.AddMinutes(15), Resume: null, "too many failures");

        var step = DvrReducer.Transition(
            cooling, new DvrEvent.StopRequested(), DomainTestData.Settings);

        var paused = Assert.IsType<DvrState.Paused>(step.Next);
        Assert.Null(paused.Finished);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Stop_while_paused_is_ignored()
    {
        var paused = new DvrState.Paused(DomainTestData.Resumable(nextPart: 3));

        var step = DvrReducer.Transition(
            paused, new DvrEvent.StopRequested(), DomainTestData.Settings);

        Assert.Same(paused, step.Next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Paused_ignores_every_event_except_resume()
    {
        var live = DomainTestData.Live();
        var session = DomainTestData.Resumable(live).Session;
        var job = new JobId(session.Id, 1);
        DvrEvent[] events =
        [
            DomainTestData.ObservedLive(live),
            DomainTestData.ObservedOffline(),
            DomainTestData.Observed(new StreamObservation.Unknown("Helix failed")),
            new DvrEvent.SessionRecovered(DomainTestData.Resumable(nextPart: 5)),
            new DvrEvent.PartProducingMedia(job, live.StartedAt),
            new DvrEvent.RecorderExited(
                job, live.StartedAt, RecorderExitKind.Exited, "late exit"),
            new DvrEvent.RetryTimerElapsed(new JobId(session.Id, 2)),
            new DvrEvent.StopRequested(),
        ];
        var paused = new DvrState.Paused(
            DomainTestData.Resumable(live, nextPart: 3, complete: true));

        foreach (var @event in events)
        {
            var step = DvrReducer.Transition(paused, @event, DomainTestData.Settings);

            Assert.Same(paused, step.Next);
            Assert.Empty(step.Commands);
        }
    }

    [Fact]
    public void Resume_returns_idle_keeping_the_finished_session()
    {
        var finished = DomainTestData.Resumable(nextPart: 5, complete: true);
        var paused = new DvrState.Paused(finished);

        var step = DvrReducer.Transition(
            paused, new DvrEvent.ResumeRequested(), DomainTestData.Settings);

        var idle = Assert.IsType<DvrState.Idle>(step.Next);
        Assert.Equal(finished, idle.Finished);
        Assert.Null(idle.Resume);
        Assert.Equal(0, idle.Streak);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Resume_from_an_empty_pause_returns_empty_idle()
    {
        var step = DvrReducer.Transition(
            new DvrState.Paused(), new DvrEvent.ResumeRequested(), DomainTestData.Settings);

        var idle = Assert.IsType<DvrState.Idle>(step.Next);
        Assert.Null(idle.Resume);
        Assert.Null(idle.Finished);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Stop_then_resume_reopens_the_finalized_session_for_the_same_stream()
    {
        var live = DomainTestData.Live();
        var recording = DomainTestData.Recording(live: live, part: 2);

        var stopped = DvrReducer.Transition(
            recording, new DvrEvent.StopRequested(), DomainTestData.Settings);
        var paused = Assert.IsType<DvrState.Paused>(stopped.Next);

        var resumed = DvrReducer.Transition(
            paused, new DvrEvent.ResumeRequested(), DomainTestData.Settings);
        var idle = Assert.IsType<DvrState.Idle>(resumed.Next);
        Assert.Equal(new Resumable(recording.Session, 3), idle.Finished);
        Assert.Null(idle.Resume);

        var observed = DvrReducer.Transition(
            idle, DomainTestData.ObservedLive(live), DomainTestData.Settings);
        var reopened = Assert.IsType<DvrState.Recording>(observed.Next);
        Assert.Equal(recording.Session, reopened.Session);
        Assert.Equal(new JobId(recording.Session.Id, 3), reopened.Job);

        Assert.Collection(
            observed.Commands,
            command => Assert.Equal(
                new DvrCommand.ReopenSession(recording.Session), command),
            command => Assert.Equal(
                new DvrCommand.RecordMetadata(recording.Session, live), command),
            command => Assert.Equal(
                new DvrCommand.StartRecorder(recording.Session, reopened.Job), command));
    }

    [Fact]
    public void An_offline_blip_then_the_same_stream_reopens_the_finalized_session()
    {
        var waiting = DomainTestData.WaitingToRetry(nextPart: 3, offlineCount: 1);
        var live = waiting.Latest;

        var offline = DvrReducer.Transition(
            waiting, DomainTestData.ObservedOffline(), DomainTestData.Settings);
        var idle = Assert.IsType<DvrState.Idle>(offline.Next);
        Assert.Equal(new Resumable(waiting.Session, 3), idle.Finished);

        var observed = DvrReducer.Transition(
            idle, DomainTestData.ObservedLive(live), DomainTestData.Settings);
        var reopened = Assert.IsType<DvrState.Recording>(observed.Next);
        Assert.Equal(waiting.Session, reopened.Session);
        Assert.Equal(new JobId(waiting.Session.Id, 3), reopened.Job);

        Assert.Collection(
            observed.Commands,
            command => Assert.IsType<DvrCommand.ReopenSession>(command),
            command => Assert.IsType<DvrCommand.RecordMetadata>(command),
            command => Assert.IsType<DvrCommand.StartRecorder>(command));
    }

    [Fact]
    public void Resume_requested_outside_paused_changes_nothing()
    {
        var recording = DomainTestData.Recording();
        var resume = DomainTestData.Resumable(nextPart: 2);
        DvrState[] states =
        [
            new DvrState.Idle(),
            new DvrState.Idle(Resume: resume, Streak: 1),
            recording,
            new DvrState.WaitingToRetry(
                recording.Session,
                new JobId(recording.Session.Id, 2),
                recording.Latest,
                Attempt: 1,
                Streak: 1),
            new DvrState.CoolingDown(
                DomainTestData.StartedAt.AddMinutes(15), resume, "previous failure"),
        ];

        foreach (var state in states)
        {
            var step = DvrReducer.Transition(
                state, new DvrEvent.ResumeRequested(), DomainTestData.Settings);

            Assert.Same(state, step.Next);
            Assert.Empty(step.Commands);
        }
    }
}
