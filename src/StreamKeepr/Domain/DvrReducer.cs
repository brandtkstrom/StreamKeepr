using System.Collections.Immutable;

namespace StreamKeepr.Domain;

/// <summary>
/// The pure per-channel transition function. It has no clock, randomness, or I/O; all time and
/// identifiers arrive in events and settings.
/// </summary>
/// <remarks>
/// This slice implements the core recording path only. Retry, streak, and cooldown handling
/// (#23) and stop, resume, and recovery handling (#24) are intentional no-ops: the reducer stays
/// in the current state and emits no commands instead of throwing.
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
            (DvrState.Idle idle, DvrEvent.StreamStatusObserved { Observation: StreamObservation.Live live })
                => IdleObservedLive(idle, live.Stream, settings),
            (DvrState.Recording recording, DvrEvent.StreamStatusObserved { Observation: StreamObservation.Live live })
                => RecordingObservedLive(recording, live.Stream, settings),
            (DvrState.Recording recording, DvrEvent.PartProducingMedia media)
                => RecordingProducedMedia(recording, media),
            _ => Stay(state),
        };
    }

    private static Step IdleObservedLive(DvrState.Idle idle, LiveStream live, DvrSettings settings)
    {
        // Reopening or resuming a recovered session belongs to the recovery path (#24).
        if (idle.Resume is not null || idle.Finished is not null)
            return Stay(idle);

        var session = Session.For(live, settings.Channel, settings.RecordingsRoot);
        var job = new JobId(session.Id, 1);
        var next = new DvrState.Recording(
            session, job, live, Attempt: 0, HasMedia: false, Streak: 0);

        return new Step(next, ImmutableArray.Create<DvrCommand>(
            new DvrCommand.OpenSession(session, live),
            new DvrCommand.StartRecorder(session, job)));
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
        var session = Session.For(live, settings.Channel, settings.RecordingsRoot);
        var job = new JobId(session.Id, 1);
        var next = new DvrState.Recording(
            session, job, live, Attempt: 0, HasMedia: false, Streak: 0);

        return new Step(next, ImmutableArray.Create<DvrCommand>(
            new DvrCommand.StopRecorder(recording.Job),
            new DvrCommand.FinalizeSession(recording.Session),
            new DvrCommand.OpenSession(session, live),
            new DvrCommand.StartRecorder(session, job)));
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
    /// The metadata predicate from the domain contract: title or game changes, or the first
    /// known thumbnail, are recorded. A known thumbnail changing to another is not.
    /// </summary>
    private static bool MetadataChanged(LiveStream current, LiveStream incoming) =>
        current.Title != incoming.Title
        || current.Game != incoming.Game
        || (current.ThumbnailUrl is null && incoming.ThumbnailUrl is not null);

    private static Step Stay(DvrState state) => new(state, ImmutableArray<DvrCommand>.Empty);
}
