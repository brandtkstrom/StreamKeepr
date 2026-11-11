using StreamKeepr.Domain;

namespace StreamKeepr.Tests.Domain;

public sealed class DvrReducerCoreTests
{
    [Fact]
    public void Live_from_idle_without_resumable_opens_the_session_and_starts_part_one()
    {
        var live = DomainTestData.Live();

        var step = DvrReducer.Transition(
            new DvrState.Idle(),
            DomainTestData.ObservedLive(live),
            DomainTestData.Settings);

        var recording = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(live, recording.Latest);
        Assert.Equal("20261110T163000Z_123456789", recording.Session.Id);
        Assert.Equal(DomainTestData.Channel, recording.Session.Channel);
        Assert.Equal(
            Path.Combine(DomainTestData.RecordingsRoot, DomainTestData.Channel, recording.Session.Id),
            recording.Session.Directory);
        Assert.Equal(new JobId(recording.Session.Id, 1), recording.Job);
        Assert.Equal(0, recording.Attempt);
        Assert.Equal(0, recording.Streak);
        Assert.False(recording.HasMedia);
        Assert.Null(recording.FirstMediaAt);

        Assert.Collection(
            step.Commands,
            command =>
            {
                var open = Assert.IsType<DvrCommand.OpenSession>(command);
                Assert.Equal(recording.Session, open.Session);
                Assert.Equal(live, open.Stream);
            },
            command =>
            {
                var start = Assert.IsType<DvrCommand.StartRecorder>(command);
                Assert.Equal(recording.Session, start.Session);
                Assert.Equal(recording.Job, start.Job);
            });
    }

    [Fact]
    public void Part_producing_media_sets_has_media_first_media_at_and_resets_attempt()
    {
        var recording = DomainTestData.Recording(attempt: 2);
        var mediaAt = recording.Latest.StartedAt.AddMinutes(5);

        var step = DvrReducer.Transition(
            recording,
            new DvrEvent.PartProducingMedia(recording.Job, mediaAt),
            DomainTestData.Settings);

        var next = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.True(next.HasMedia);
        Assert.Equal(mediaAt, next.FirstMediaAt);
        Assert.Equal(0, next.Attempt);
        Assert.Equal(recording.Session, next.Session);
        Assert.Equal(recording.Job, next.Job);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Part_producing_media_is_ignored_after_the_first_media_event()
    {
        var firstMediaAt = DomainTestData.StartedAt.AddMinutes(1);
        var recording = DomainTestData.Recording(hasMedia: true, firstMediaAt: firstMediaAt);

        var step = DvrReducer.Transition(
            recording,
            new DvrEvent.PartProducingMedia(recording.Job, firstMediaAt.AddMinutes(1)),
            DomainTestData.Settings);

        Assert.Same(recording, step.Next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Part_producing_media_for_another_job_is_ignored()
    {
        var recording = DomainTestData.Recording();

        var step = DvrReducer.Transition(
            recording,
            new DvrEvent.PartProducingMedia(
                new JobId(recording.Session.Id, 2), DomainTestData.StartedAt),
            DomainTestData.Settings);

        Assert.Same(recording, step.Next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void Title_change_while_recording_emits_one_record_metadata()
    {
        var recording = DomainTestData.Recording();
        var changed = recording.Latest with { Title = "A new title" };

        var step = DvrReducer.Transition(
            recording, DomainTestData.ObservedLive(changed), DomainTestData.Settings);

        var next = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(changed, next.Latest);
        var metadata = Assert.IsType<DvrCommand.RecordMetadata>(Assert.Single(step.Commands));
        Assert.Equal(recording.Session, metadata.Session);
        Assert.Equal(changed, metadata.Stream);
    }

    [Fact]
    public void Game_change_while_recording_emits_one_record_metadata()
    {
        var recording = DomainTestData.Recording();
        var changed = recording.Latest with { Game = "A new game" };

        var step = DvrReducer.Transition(
            recording, DomainTestData.ObservedLive(changed), DomainTestData.Settings);

        var next = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(changed, next.Latest);
        var metadata = Assert.IsType<DvrCommand.RecordMetadata>(Assert.Single(step.Commands));
        Assert.Equal(recording.Session, metadata.Session);
        Assert.Equal(changed, metadata.Stream);
    }

    [Fact]
    public void Title_and_game_change_in_one_observation_emit_one_record_metadata()
    {
        var recording = DomainTestData.Recording();
        var changed = recording.Latest with { Title = "A new title", Game = "A new game" };

        var step = DvrReducer.Transition(
            recording, DomainTestData.ObservedLive(changed), DomainTestData.Settings);

        var next = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(changed, next.Latest);
        Assert.IsType<DvrCommand.RecordMetadata>(Assert.Single(step.Commands));
    }

    [Fact]
    public void Unchanged_stream_details_while_recording_emit_no_commands()
    {
        var recording = DomainTestData.Recording();

        var step = DvrReducer.Transition(
            recording, DomainTestData.ObservedLive(recording.Latest), DomainTestData.Settings);

        Assert.Same(recording, step.Next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void First_known_thumbnail_while_recording_emits_one_record_metadata()
    {
        var recording = DomainTestData.Recording();
        var changed = recording.Latest with { ThumbnailUrl = "https://example.test/thumb.jpg" };

        var step = DvrReducer.Transition(
            recording, DomainTestData.ObservedLive(changed), DomainTestData.Settings);

        Assert.IsType<DvrCommand.RecordMetadata>(Assert.Single(step.Commands));
    }

    [Fact]
    public void A_different_known_thumbnail_while_recording_emits_no_commands()
    {
        var recording = DomainTestData.Recording(
            live: DomainTestData.Live(thumbnailUrl: "https://example.test/first.jpg"));
        var changed = recording.Latest with { ThumbnailUrl = "https://example.test/second.jpg" };

        var step = DvrReducer.Transition(
            recording, DomainTestData.ObservedLive(changed), DomainTestData.Settings);

        Assert.Same(recording, step.Next);
        Assert.Empty(step.Commands);
    }

    [Fact]
    public void New_stream_id_while_recording_stops_finalizes_opens_and_starts_in_order()
    {
        var recording = DomainTestData.Recording();
        var replacement = DomainTestData.Live(
            streamId: "987654321",
            title: "Second stream",
            game: "Another game",
            startedAt: DomainTestData.StartedAt.AddMinutes(10));

        var step = DvrReducer.Transition(
            recording, DomainTestData.ObservedLive(replacement), DomainTestData.Settings);

        var next = Assert.IsType<DvrState.Recording>(step.Next);
        Assert.Equal(replacement, next.Latest);
        Assert.Equal("20261110T164000Z_987654321", next.Session.Id);
        Assert.Equal(new JobId(next.Session.Id, 1), next.Job);
        Assert.Equal(0, next.Attempt);
        Assert.False(next.HasMedia);
        Assert.Null(next.FirstMediaAt);

        Assert.Collection(
            step.Commands,
            command => Assert.Equal(new DvrCommand.StopRecorder(recording.Job), command),
            command => Assert.Equal(new DvrCommand.FinalizeSession(recording.Session), command),
            command => Assert.Equal(new DvrCommand.OpenSession(next.Session, replacement), command),
            command => Assert.Equal(new DvrCommand.StartRecorder(next.Session, next.Job), command));
    }

    [Fact]
    public void Unknown_never_changes_state_or_emits_commands()
    {
        var recording = DomainTestData.Recording();
        var resume = new Resumable(recording.Session, NextPart: 2);
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
            new DvrState.Paused(resume),
        ];
        var unknown = DomainTestData.Observed(
            new StreamObservation.Unknown("Helix request failed"),
            DomainTestData.StartedAt.AddMinutes(1));

        foreach (var state in states)
        {
            var step = DvrReducer.Transition(state, unknown, DomainTestData.Settings);

            Assert.Same(state, step.Next);
            Assert.Empty(step.Commands);
        }
    }

    [Fact]
    public void One_offline_while_recording_is_advisory()
    {
        var recording = DomainTestData.Recording();

        var step = DvrReducer.Transition(
            recording,
            DomainTestData.Observed(
                new StreamObservation.Offline(), DomainTestData.StartedAt.AddMinutes(1)),
            DomainTestData.Settings);

        Assert.Same(recording, step.Next);
        Assert.Empty(step.Commands);
    }
}
