using System.Collections.Immutable;

namespace StreamKeepr.Domain;

/// <summary>
/// The pure per-channel transition function. It has no clock, randomness, or I/O; all time and
/// identifiers arrive in events and settings.
/// </summary>
/// <remarks>
/// This slice implements the recording, retry, streak, and cooldown paths. Stop, resume, and
/// recovery handling (#24) are intentional no-ops: the reducer stays in the current state and
/// emits no commands instead of throwing.
/// </remarks>
public static class DvrReducer
{
    public static Step Transition(DvrState state, DvrEvent @event, DvrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(settings);

        return (state, @event) switch
        {
            (DvrState.Idle idle, DvrEvent.StreamStatusObserved observed)
                => IdleObserved(idle, observed.Observation, settings),
            (DvrState.Recording recording, DvrEvent.StreamStatusObserved { Observation: StreamObservation.Live live })
                => RecordingObservedLive(recording, live.Stream, settings),
            (DvrState.Recording recording, DvrEvent.PartProducingMedia media)
                => RecordingProducedMedia(recording, media),
            (DvrState.Recording recording, DvrEvent.RecorderExited exit)
                => RecordingExited(recording, exit, settings),
            (DvrState.WaitingToRetry waiting, DvrEvent.StreamStatusObserved observed)
                => WaitingToRetryObserved(waiting, observed.Observation, settings),
            (DvrState.WaitingToRetry waiting, DvrEvent.RetryTimerElapsed timer)
                => WaitingToRetryRetryElapsed(waiting, timer),
            (DvrState.CoolingDown cooling, DvrEvent.StreamStatusObserved observed)
                => CoolingDownObserved(cooling, observed.Observation, observed.At, settings),
            _ => Stay(state),
        };
    }

    private static Step IdleObserved(
        DvrState.Idle idle, StreamObservation observation, DvrSettings settings) =>
        observation is StreamObservation.Live { Stream: var live }
            ? IdleObservedLive(idle, live, settings)
            : Stay(idle);

    private static Step IdleObservedLive(DvrState.Idle idle, LiveStream live, DvrSettings settings)
    {
        // Reopening or resuming a recovered session belongs to the recovery path (#24).
        if (idle.Resume is not null || idle.Finished is not null)
            return Stay(idle);

        return StartNewRecording(
            live, settings, idle.Streak, ImmutableArray<DvrCommand>.Empty);
    }

    private static Step RecordingObservedLive(
        DvrState.Recording recording, LiveStream live, DvrSettings settings)
    {
        if (live.StreamId != recording.Latest.StreamId)
            return ReplaceStream(recording, live, settings);

        if (!MetadataChanged(recording.Latest, live))
            return Stay(recording);

        return new Step(
            recording with { Latest = live },
            ImmutableArray.Create<DvrCommand>(
                new DvrCommand.RecordMetadata(recording.Session, live)));
    }

    private static Step ReplaceStream(
        DvrState.Recording recording, LiveStream live, DvrSettings settings)
    {
        var before = ImmutableArray.Create<DvrCommand>(
            new DvrCommand.StopRecorder(recording.Job),
            new DvrCommand.FinalizeSession(recording.Session));

        return StartNewRecording(live, settings, recording.Streak, before);
    }

    private static Step RecordingProducedMedia(
        DvrState.Recording recording, DvrEvent.PartProducingMedia media)
    {
        if (media.Job != recording.Job || recording.HasMedia)
            return Stay(recording);

        return new Step(
            recording with { HasMedia = true, FirstMediaAt = media.At, Attempt = 0 },
            ImmutableArray<DvrCommand>.Empty);
    }

    /// <summary>
    /// The after-exit algorithm from the domain contract. A self-exit with enough media is a
    /// healthy part and resets the streak; anything else builds the streak toward cooldown and
    /// uses the retry budget for the next part.
    /// </summary>
    private static Step RecordingExited(
        DvrState.Recording recording, DvrEvent.RecorderExited exit, DvrSettings settings)
    {
        // Stopped while Recording is a supervisor contract violation: the reducer stays put.
        if (exit.Job != recording.Job || exit.Kind == RecorderExitKind.Stopped)
            return Stay(recording);

        bool healthy = exit.Kind == RecorderExitKind.Exited
            && recording.HasMedia
            && recording.FirstMediaAt is { } firstMediaAt
            && exit.At - firstMediaAt >= settings.HealthyPart;
        int attempt = recording.HasMedia ? 1 : recording.Attempt + 1;
        int streak = healthy ? 0 : recording.Streak + 1;
        int nextPart = recording.Job.Part + 1;
        var resume = new Resumable(recording.Session, nextPart);

        if (!healthy && streak >= settings.FailuresBeforeCooldown)
        {
            return new Step(
                new DvrState.CoolingDown(exit.At + settings.Cooldown, resume, exit.Detail),
                ImmutableArray<DvrCommand>.Empty);
        }

        if (attempt <= settings.MaxRetries)
        {
            var next = new JobId(recording.Session.Id, nextPart);
            return new Step(
                new DvrState.WaitingToRetry(
                    recording.Session, next, recording.Latest, attempt, streak),
                ImmutableArray.Create<DvrCommand>(
                    new DvrCommand.ScheduleRetry(next, Backoff(attempt, settings))));
        }

        return new Step(
            new DvrState.Idle(resume, streak),
            ImmutableArray<DvrCommand>.Empty);
    }

    private static Step WaitingToRetryObserved(
        DvrState.WaitingToRetry waiting, StreamObservation observation, DvrSettings settings)
    {
        switch (observation)
        {
            case StreamObservation.Live { Stream: var live } when live.StreamId == waiting.Session.StreamId:
            {
                bool metadataChanged = MetadataChanged(waiting.Latest, live);
                if (!metadataChanged && waiting.OfflineCount == 0)
                    return Stay(waiting);

                var next = waiting with { Latest = live, OfflineCount = 0 };
                return metadataChanged
                    ? new Step(
                        next,
                        ImmutableArray.Create<DvrCommand>(
                            new DvrCommand.RecordMetadata(waiting.Session, live)))
                    : new Step(next, ImmutableArray<DvrCommand>.Empty);
            }

            case StreamObservation.Live { Stream: var live }:
                return StartNewRecording(
                    live,
                    settings,
                    waiting.Streak,
                    ImmutableArray.Create<DvrCommand>(
                        new DvrCommand.FinalizeSession(waiting.Session)));

            case StreamObservation.Offline when waiting.OfflineCount >= 1:
                return new Step(
                    new DvrState.Idle(
                        Finished: new Resumable(waiting.Session, waiting.Next.Part)),
                    ImmutableArray.Create<DvrCommand>(
                        new DvrCommand.FinalizeSession(waiting.Session)));

            case StreamObservation.Offline:
                return new Step(
                    waiting with { OfflineCount = 1 },
                    ImmutableArray<DvrCommand>.Empty);

            default:
                return Stay(waiting);
        }
    }

    private static Step WaitingToRetryRetryElapsed(
        DvrState.WaitingToRetry waiting, DvrEvent.RetryTimerElapsed timer)
    {
        // A retry timer after an offline observation must not launch a recorder. Stale timers
        // for another job also change nothing.
        if (timer.Job != waiting.Next || waiting.OfflineCount > 0)
            return Stay(waiting);

        // Attempt carries into the retried part so repeated no-media parts exhaust MaxRetries.
        var next = new DvrState.Recording(
            waiting.Session,
            waiting.Next,
            waiting.Latest,
            waiting.Attempt,
            HasMedia: false,
            waiting.Streak);

        return new Step(
            next,
            ImmutableArray.Create<DvrCommand>(
                new DvrCommand.StartRecorder(waiting.Session, waiting.Next)));
    }

    private static Step CoolingDownObserved(
        DvrState.CoolingDown cooling,
        StreamObservation observation,
        DateTimeOffset at,
        DvrSettings settings)
    {
        // Unknown is never evidence that the cooldown has ended.
        if (observation is StreamObservation.Unknown || at < cooling.Until)
            return Stay(cooling);

        return IdleObserved(
            new DvrState.Idle(cooling.Resume, Streak: 0), observation, settings);
    }

    /// <summary>
    /// Creates a new session at part one. <paramref name="streak"/> carries the failure streak
    /// across a stream change; only a healthy part or a normal offline finalize resets it.
    /// </summary>
    private static Step StartNewRecording(
        LiveStream live,
        DvrSettings settings,
        int streak,
        ImmutableArray<DvrCommand> before)
    {
        var session = Session.For(live, settings.Channel, settings.RecordingsRoot);
        var job = new JobId(session.Id, 1);
        var next = new DvrState.Recording(
            session, job, live, Attempt: 0, HasMedia: false, streak);
        var commands = before
            .Add(new DvrCommand.OpenSession(session, live))
            .Add(new DvrCommand.StartRecorder(session, job));

        return new Step(next, commands);
    }

    /// <summary>
    /// <c>min(RetryMax, RetryBase * 2^(attempt - 1))</c>, doubled with a cap so large attempt
    /// values cannot overflow <see cref="TimeSpan"/>.
    /// </summary>
    private static TimeSpan Backoff(int attempt, DvrSettings settings)
    {
        var delay = settings.RetryBase;
        for (var i = 1; i < attempt && delay < settings.RetryMax; i++)
        {
            delay = delay.Ticks <= settings.RetryMax.Ticks - delay.Ticks
                ? delay + delay
                : settings.RetryMax;
        }

        return delay < settings.RetryMax ? delay : settings.RetryMax;
    }

    /// <summary>
    /// The metadata predicate from the domain contract: title or game changes, or the first
    /// known thumbnail, are recorded. A known thumbnail changing to another is not.
    /// </summary>
    private static bool MetadataChanged(LiveStream current, LiveStream incoming) =>
        current.Title != incoming.Title
        || current.Game != incoming.Game
        || (current.ThumbnailUrl is null && incoming.ThumbnailUrl is not null);

    private static Step Stay(DvrState state) => new(state, ImmutableArray<DvrCommand>.Empty);
}
