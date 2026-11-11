using System.Collections.Immutable;
using StreamKeepr.Domain;

namespace StreamKeepr.Tests.Domain;

public sealed class DvrReducerPropertyTests
{
    private static DvrSettings Settings => DomainTestData.Settings;

    [Fact]
    public void Transition_is_total_and_preserves_state_invariants_for_representative_pairs()
    {
        foreach (var state in States())
        {
            foreach (var @event in Events())
            {
                Step step;
                try
                {
                    step = DvrReducer.Transition(state, @event, Settings);
                }
                catch (Exception exception)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"Transition threw for state {state} and event {@event}: {exception}");
                }

                AssertInvariants(step.Next, state, @event);
            }
        }
    }

    [Fact]
    public void Transition_is_deterministic_for_representative_pairs()
    {
        foreach (var state in States())
        {
            foreach (var @event in Events())
            {
                var first = DvrReducer.Transition(state, @event, Settings);
                var second = DvrReducer.Transition(state, @event, Settings);

                Assert.Equal(first.Next, second.Next);
                AssertCommandsEqual(first.Commands, second.Commands);
            }
        }
    }

    [Fact]
    public void Stale_job_events_emit_no_commands_in_any_representative_state()
    {
        var stale = new JobId("20260101T000000Z_000000000", 9);
        var at = DomainTestData.StartedAt.AddMinutes(1);
        DvrEvent[] staleEvents =
        [
            new DvrEvent.PartProducingMedia(stale, at),
            new DvrEvent.RecorderExited(stale, at, RecorderExitKind.Exited, "late"),
            new DvrEvent.RecorderExited(stale, at, RecorderExitKind.Killed, "late"),
            new DvrEvent.RetryTimerElapsed(stale),
        ];

        foreach (var state in States())
        {
            foreach (var @event in staleEvents)
            {
                var step = DvrReducer.Transition(state, @event, Settings);

                Assert.Empty(step.Commands);
            }
        }
    }

    private static void AssertInvariants(DvrState next, DvrState before, DvrEvent @event)
    {
        switch (next)
        {
            case DvrState.Idle idle:
                // A stable no-op may preserve an externally constructed Idle that already has
                // both; the reducer itself must never build such a state.
                if (!ReferenceEquals(next, before))
                {
                    Assert.False(
                        idle.Resume is not null && idle.Finished is not null,
                        $"Idle produced both Resume and Finished after {before} + {@event}.");
                }

                Assert.True(
                    idle.Streak >= 0,
                    $"Idle.Streak is negative after {before} + {@event}.");
                break;

            case DvrState.Recording recording:
                Assert.Equal(recording.HasMedia, recording.FirstMediaAt is not null);
                Assert.True(
                    recording.Attempt >= 0,
                    $"Recording.Attempt is negative after {before} + {@event}.");
                Assert.True(
                    recording.Streak >= 0,
                    $"Recording.Streak is negative after {before} + {@event}.");
                break;

            case DvrState.WaitingToRetry waiting:
                Assert.True(
                    waiting.Attempt >= 0,
                    $"WaitingToRetry.Attempt is negative after {before} + {@event}.");
                Assert.True(
                    waiting.Streak >= 0,
                    $"WaitingToRetry.Streak is negative after {before} + {@event}.");
                Assert.True(
                    waiting.OfflineCount >= 0,
                    $"WaitingToRetry.OfflineCount is negative after {before} + {@event}.");
                break;
        }
    }

    private static void AssertCommandsEqual(
        ImmutableArray<DvrCommand> expected, ImmutableArray<DvrCommand> actual)
    {
        Assert.Equal(expected.Length, actual.Length);

        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i]);
    }

    private static IEnumerable<DvrState> States()
    {
        var live = DomainTestData.Live();
        var otherLive = DomainTestData.Live(
            streamId: "987654321",
            title: "Another stream",
            startedAt: DomainTestData.StartedAt.AddMinutes(10));
        var resume = DomainTestData.Resumable(live, nextPart: 3);
        var otherResume = DomainTestData.Resumable(otherLive, nextPart: 2);
        var finished = DomainTestData.Resumable(live, nextPart: 4, complete: true);
        var coolingUntil = live.StartedAt.AddMinutes(15);

        yield return new DvrState.Idle();
        yield return new DvrState.Idle(Resume: resume, Streak: 1);
        yield return new DvrState.Idle(Streak: 2, Finished: finished);
        // Both may be constructed externally; Finished must win and never throw.
        yield return new DvrState.Idle(Resume: otherResume, Streak: 3, Finished: finished);
        yield return DomainTestData.Recording(
            live: live, attempt: 2, streak: 1, part: 2);
        yield return DomainTestData.Recording(
            live: live,
            attempt: 0,
            hasMedia: true,
            streak: 0,
            firstMediaAt: live.StartedAt.AddSeconds(30),
            part: 2);
        yield return DomainTestData.WaitingToRetry(
            live: live, attempt: 2, streak: 2, nextPart: 3);
        yield return DomainTestData.WaitingToRetry(
            live: live, attempt: 2, streak: 2, nextPart: 3, offlineCount: 1);
        yield return new DvrState.CoolingDown(coolingUntil, resume, "repeated failures");
        yield return new DvrState.CoolingDown(
            coolingUntil, Resume: null, "repeated failures");
        yield return new DvrState.Paused();
        yield return new DvrState.Paused(finished);
    }

    private static IEnumerable<DvrEvent> Events()
    {
        var live = DomainTestData.Live();
        var otherLive = DomainTestData.Live(
            streamId: "987654321",
            title: "Another stream",
            startedAt: DomainTestData.StartedAt.AddMinutes(10));
        var session = Session.For(live, DomainTestData.Channel, DomainTestData.RecordingsRoot);
        var at = DomainTestData.StartedAt.AddMinutes(1);

        yield return DomainTestData.ObservedLive(live, at);
        yield return DomainTestData.ObservedLive(otherLive, at);
        yield return DomainTestData.ObservedOffline(at);
        yield return DomainTestData.Observed(
            new StreamObservation.Unknown("Helix failed"), at);
        yield return new DvrEvent.SessionRecovered(
            DomainTestData.Resumable(live, nextPart: 5));
        yield return new DvrEvent.SessionRecovered(
            DomainTestData.Resumable(live, nextPart: 5, complete: true));
        yield return new DvrEvent.PartProducingMedia(new JobId(session.Id, 2), at);
        yield return new DvrEvent.PartProducingMedia(new JobId(session.Id, 9), at);
        yield return new DvrEvent.RecorderExited(
            new JobId(session.Id, 2), at, RecorderExitKind.Exited, "stream ended");
        yield return new DvrEvent.RecorderExited(
            new JobId(session.Id, 2), at, RecorderExitKind.Stopped, "stop requested");
        yield return new DvrEvent.RecorderExited(
            new JobId(session.Id, 2), at, RecorderExitKind.Killed, "watchdog");
        yield return new DvrEvent.RecorderExited(
            new JobId(session.Id, 9), at, RecorderExitKind.Exited, "stale");
        yield return new DvrEvent.RetryTimerElapsed(new JobId(session.Id, 3));
        yield return new DvrEvent.RetryTimerElapsed(new JobId(session.Id, 9));
        yield return new DvrEvent.StopRequested();
        yield return new DvrEvent.ResumeRequested();
    }
}
