using System.Text.Json;
using System.Text.Json.Nodes;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class SessionStoreRecordTests
{
    [Fact]
    public async Task Open_records_a_recording_session_with_its_first_metadata_and_writes_no_manifest()
    {
        using var context = new SessionStoreTestContext();

        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        Assert.True(Directory.Exists(context.Directory));
        Assert.False(File.Exists(Path.Combine(context.Directory, "session.json")));

        SessionRecord? record = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(SessionStatus.Recording, record.Status);
        Assert.Equal(context.Session.Id, record.Id);
        Assert.Equal("streamer", record.Channel);
        Assert.Equal("123456789", record.StreamId);
        Assert.Equal(context.Session.StartedAt, record.StartedAt);
        Assert.Null(record.EndedAt);
        Assert.Null(record.DurationSeconds);
        Assert.Null(record.SizeBytes);
        Assert.Null(record.PartCount);
        Assert.Null(record.SegmentCount);
        Assert.False(record.HasThumbnail);

        MetadataChange first = Assert.Single(record.Metadata);
        Assert.Equal("A stream", first.Title);
        Assert.Equal("Just Chatting", first.Game);
        Assert.Equal(SessionStoreTestContext.Now, first.At);
    }

    [Fact]
    public async Task TryLoad_returns_the_metadata_history_in_order_with_the_latest_last()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata("New title", "New game"), CancellationToken.None);

        SessionRecord? record = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);

        Assert.NotNull(record);
        Assert.Collection(
            record.Metadata,
            first =>
            {
                Assert.Equal("A stream", first.Title);
                Assert.Equal("Just Chatting", first.Game);
            },
            second =>
            {
                Assert.Equal("New title", second.Title);
                Assert.Equal("New game", second.Game);
                Assert.Equal(SessionStoreTestContext.Now.AddMinutes(1), second.At);
            });
    }

    [Fact]
    public async Task TryLoad_returns_null_for_a_session_without_a_record()
    {
        using var context = new SessionStoreTestContext();

        Assert.Null(await context.Store.TryLoadAsync(context.Directory, CancellationToken.None));

        // A folder alone, such as a legacy recording, is not a session.
        Directory.CreateDirectory(context.Directory);
        await File.WriteAllTextAsync(Path.Combine(context.Directory, "session.json"), "{}");
        Assert.Null(await context.Store.TryLoadAsync(context.Directory, CancellationToken.None));
    }

    [Fact]
    public async Task Open_reopens_an_existing_session_instead_of_overwriting_it()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata("Changed", "Changed game"), CancellationToken.None);

        // Finalize state that reopen must clear.
        context.WritePart(1, PlaylistTestData.Segment(
            "p001_00001.m4s", "2026-11-10T16:30:00Z", 6));
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        Assert.True(File.Exists(context.IndexPath));

        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await context.Store.OpenAsync(
            context.Session, context.Metadata("Ignored on reopen", "Ignored"), CancellationToken.None);

        SessionRecord? record = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(SessionStatus.Recording, record.Status);
        Assert.Null(record.EndedAt);
        Assert.Null(record.DurationSeconds);
        Assert.Null(record.SizeBytes);
        Assert.Null(record.PartCount);
        Assert.Null(record.SegmentCount);
        Assert.False(File.Exists(context.IndexPath));
        Assert.Equal(2, record.Metadata.Count);
        Assert.Equal("Changed", record.Metadata[^1].Title);
        Assert.Equal("Changed game", record.Metadata[^1].Game);
    }

    [Fact]
    public async Task Open_waits_for_the_session_lock_without_nesting_it()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        // Open on an existing session must call the lock-free reopen core. If it called the
        // public ReopenAsync after taking the lock, this second open would wait forever.
        var gate = context.Store.LockFor(context.Directory);
        await gate.WaitAsync();
        try
        {
            var open = context.Store.OpenAsync(
                context.Session, context.Live, CancellationToken.None);
            Assert.False(open.IsCompleted);

            gate.Release();
            await open.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (gate.CurrentCount == 0)
                gate.Release();
        }

        SessionRecord? record = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Single(record.Metadata);
    }

    [Fact]
    public async Task A_failed_open_throws_and_records_nothing()
    {
        using var context = new SessionStoreTestContext();
        context.Records.WritesFailing = true;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None));

        context.Records.WritesFailing = false;
        Assert.Null(await context.Store.TryLoadAsync(context.Directory, CancellationToken.None));
        Assert.Empty(context.Library.All);

        // The next attempt opens normally over the folder the failed one left.
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        Assert.NotNull(await context.Store.TryLoadAsync(context.Directory, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_metadata_write_throws_and_leaves_the_record_and_library_unchanged()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.Records.WritesFailing = true;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Store.AppendMetadataAsync(
                context.Session, context.Metadata("Never written", "Never"), CancellationToken.None));

        context.Records.WritesFailing = false;
        SessionRecord? record = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal("A stream", Assert.Single(record.Metadata).Title);
        Assert.Equal("A stream", Assert.Single(context.Library.All).Title);
    }

    [Fact]
    public async Task A_failed_finalize_leaves_the_session_open_in_the_record_and_library()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.WritePart(1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:30:00Z", 6));
        context.Records.WritesFailing = true;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Store.FinalizeAsync(context.Session, CancellationToken.None));

        context.Records.WritesFailing = false;
        SessionRecord? record = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(SessionStatus.Recording, record.Status);
        Assert.Null(record.DurationSeconds);
        Assert.True(Assert.Single(context.Library.All).IsRecording);
    }

    [Fact]
    public async Task A_failed_reopen_leaves_the_session_complete_with_its_index()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        context.WritePart(1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:30:00Z", 6));
        await context.Store.FinalizeAsync(context.Session, CancellationToken.None);
        context.Records.WritesFailing = true;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Store.ReopenAsync(context.Session, CancellationToken.None));

        context.Records.WritesFailing = false;
        SessionRecord? record = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal(SessionStatus.Complete, record.Status);
        Assert.Equal(6.0, record.DurationSeconds);
        Assert.True(File.Exists(context.IndexPath));
        Assert.False(Assert.Single(context.Library.All).IsRecording);
    }

    [Fact]
    public async Task Open_records_one_session_under_concurrent_calls()
    {
        using var context = new SessionStoreTestContext();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        SessionRecord? record = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(record);
        var entry = Assert.Single(record.Metadata);
        Assert.Equal("A stream", entry.Title);
        Assert.Equal("Just Chatting", entry.Game);
        Assert.Equal(SessionStoreTestContext.Now, entry.At);
    }
}
