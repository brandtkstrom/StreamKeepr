using StreamKeepr.Domain;

namespace StreamKeepr.Tests.Domain;

public sealed class DvrReducerRecoveryTests
{
    [Fact]
    public void A_complete_recovered_session_is_installed_as_idle_finished()
    {
        var recovered = DomainTestData.Resumable(nextPart: 4, complete: true);

        var step = DvrReducer.Transition(
            new DvrState.Idle(Streak: 2),
            new DvrEvent.SessionRecovered(recovered),
            DomainTestData.Settings);

        var idle = Assert.IsType<DvrState.Idle>(step.Next);
        Assert.Null(idle.Resume);
        Assert.Equal(recovered, idle.Finished);
        Assert.Equal(2, idle.Streak);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void An_unfinished_recovered_session_is_installed_as_idle_resume()
    {
        var recovered = DomainTestData.Resumable(nextPart: 3);

        var step = DvrReducer.Transition(
            new DvrState.Idle(),
            new DvrEvent.SessionRecovered(recovered),
            DomainTestData.Settings);

        var idle = Assert.IsType<DvrState.Idle>(step.Next);
        Assert.Equal(recovered, idle.Resume);
        Assert.Null(idle.Finished);
        Assert.Equal(0, idle.Streak);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Recovery_replaces_an_already_installed_resumable()
    {
        var first = DomainTestData.Resumable(nextPart: 2);
        var second = DomainTestData.Resumable(nextPart: 5, complete: true);

        var step = DvrReducer.Transition(
            new DvrState.Idle(Resume: first),
            new DvrEvent.SessionRecovered(second),
            DomainTestData.Settings);

        var idle = Assert.IsType<DvrState.Idle>(step.Next);
        Assert.Null(idle.Resume);
        Assert.Equal(second, idle.Finished);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void A_complete_recovered_session_reopens_at_the_next_part_when_the_same_stream_returns()
    {
        var live = DomainTestData.Live();
        var recovered = DomainTestData.Resumable(live, nextPart: 4, complete: true);
        var idle = new DvrState.Idle(Finished: recovered);

        var step = DvrReducer.Transition(
            idle, DomainTestData.ObservedLive(live), DomainTestData.Settings);

        var recording = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(recovered.Session, recording.Session);
        Assert.Equal(new JobId(recovered.Session.Id, 4), recording.Job);
        Assert.Equal(live, recording.Latest);
        Assert.Equal(0, recording.Attempt);
        Assert.Equal(0, recording.Streak);
        Assert.False(recording.HasMedia);
        Assert.Null(recording.FirstMediaAt);

        Assert.Collection(
            step.Commands,
            command => Assert.Equal(
                new DvrCommand.ReopenSession(recovered.Session), command),
            command => Assert.Equal(
                new DvrCommand.RecordMetadata(recovered.Session, live), command),
            command => Assert.Equal(
                new DvrCommand.StartRecorder(recovered.Session, recording.Job), command));
        Assert.DoesNotContain(step.Commands, command => command is DvrCommand.OpenSession);
    }

    [Fact]
    public void An_unfinished_recovered_session_resumes_at_the_next_part_when_the_same_stream_returns()
    {
        var live = DomainTestData.Live();
        var recovered = DomainTestData.Resumable(live, nextPart: 3);
        var idle = new DvrState.Idle(Resume: recovered, Streak: 1);

        var step = DvrReducer.Transition(
            idle, DomainTestData.ObservedLive(live), DomainTestData.Settings);

        var recording = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(recovered.Session, recording.Session);
        Assert.Equal(new JobId(recovered.Session.Id, 3), recording.Job);
        Assert.Equal(live, recording.Latest);
        Assert.Equal(0, recording.Attempt);
        Assert.Equal(1, recording.Streak);

        Assert.Collection(
            step.Commands,
            command => Assert.Equal(
                new DvrCommand.RecordMetadata(recovered.Session, live), command),
            command => Assert.Equal(
                new DvrCommand.StartRecorder(recovered.Session, recording.Job), command));
        Assert.DoesNotContain(step.Commands, command => command is DvrCommand.OpenSession);
        Assert.DoesNotContain(step.Commands, command => command is DvrCommand.ReopenSession);
    }

    [Fact]
    public void A_different_stream_replaces_an_unfinished_recovered_session()
    {
        var resume = DomainTestData.Resumable(nextPart: 3);
        var idle = new DvrState.Idle(Resume: resume, Streak: 2);
        var replacement = DomainTestData.Live(
            streamId: "987654321",
            title: "Second stream",
            startedAt: DomainTestData.StartedAt.AddMinutes(20));

        var step = DvrReducer.Transition(
            idle, DomainTestData.ObservedLive(replacement), DomainTestData.Settings);

        var recording = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(replacement, recording.Latest);
        Assert.Equal(new JobId(recording.Session.Id, 1), recording.Job);
        Assert.Equal(2, recording.Streak);

        Assert.Collection(
            step.Commands,
            command => Assert.Equal(new DvrCommand.FinalizeSession(resume.Session), command),
            command => Assert.Equal(
                new DvrCommand.OpenSession(recording.Session, replacement), command),
            command => Assert.Equal(
                new DvrCommand.StartRecorder(recording.Session, recording.Job), command));
    }

    [Fact]
    public void A_different_stream_replaces_a_finished_session_without_finalizing_it()
    {
        var finished = DomainTestData.Resumable(nextPart: 3, complete: true);
        var idle = new DvrState.Idle(Finished: finished);
        var replacement = DomainTestData.Live(
            streamId: "987654321",
            title: "Second stream",
            startedAt: DomainTestData.StartedAt.AddMinutes(20));

        var step = DvrReducer.Transition(
            idle, DomainTestData.ObservedLive(replacement), DomainTestData.Settings);

        var recording = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(replacement, recording.Latest);
        Assert.Equal(new JobId(recording.Session.Id, 1), recording.Job);

        Assert.Collection(
            step.Commands,
            command => Assert.Equal(
                new DvrCommand.OpenSession(recording.Session, replacement), command),
            command => Assert.Equal(
                new DvrCommand.StartRecorder(recording.Session, recording.Job), command));
        Assert.DoesNotContain(step.Commands, command => command is DvrCommand.FinalizeSession);
    }

    [Fact]
    public void Offline_with_a_resumable_finalizes_into_idle_finished()
    {
        var resume = DomainTestData.Resumable(nextPart: 3);
        var idle = new DvrState.Idle(Resume: resume, Streak: 2);

        var step = DvrReducer.Transition(
            idle, DomainTestData.ObservedOffline(), DomainTestData.Settings);

        var next = Assert.IsType<DvrState.Idle>(step.Next);
        Assert.Null(next.Resume);
        Assert.Equal(resume, next.Finished);
        Assert.Equal(0, next.Streak);

        var finalize = Assert.IsType<DvrCommand.FinalizeSession>(Assert.Single(step.Commands));
        Assert.Equal(resume.Session, finalize.Session);
    }

    [Fact]
    public void Offline_without_a_resumable_leaves_idle_unchanged()
    {
        var idle = new DvrState.Idle(Streak: 1);

        var step = DvrReducer.Transition(
            idle, DomainTestData.ObservedOffline(), DomainTestData.Settings);

        Assert.Same(idle, step.Next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Offline_with_both_resumables_prefers_finished_and_finalizes_nothing()
    {
        var resume = DomainTestData.Resumable(nextPart: 2);
        var finished = DomainTestData.Resumable(nextPart: 4, complete: true);
        var idle = new DvrState.Idle(Resume: resume, Finished: finished);

        var step = DvrReducer.Transition(
            idle, DomainTestData.ObservedOffline(), DomainTestData.Settings);

        Assert.Same(idle, step.Next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Finished_wins_over_resume_when_both_are_constructed_externally()
    {
        var live = DomainTestData.Live();
        var otherLive = DomainTestData.Live(
            streamId: "111111111", startedAt: DomainTestData.StartedAt.AddMinutes(10));
        var resume = DomainTestData.Resumable(otherLive, nextPart: 2);
        var finished = DomainTestData.Resumable(live, nextPart: 5, complete: true);
        var idle = new DvrState.Idle(Resume: resume, Streak: 2, Finished: finished);

        var step = DvrReducer.Transition(
            idle, DomainTestData.ObservedLive(live), DomainTestData.Settings);

        var recording = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(finished.Session, recording.Session);
        Assert.Equal(new JobId(finished.Session.Id, 5), recording.Job);
        Assert.Collection(
            step.Commands,
            command => Assert.Equal(new DvrCommand.ReopenSession(finished.Session), command),
            command => Assert.Equal(
                new DvrCommand.RecordMetadata(finished.Session, live), command),
            command => Assert.Equal(
                new DvrCommand.StartRecorder(finished.Session, recording.Job), command));
        Assert.DoesNotContain(
            step.Commands,
            command => command is DvrCommand.FinalizeSession { Session: var session }
                && session == resume.Session);
    }
}
