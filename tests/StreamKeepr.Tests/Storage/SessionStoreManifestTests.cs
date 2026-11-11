using System.Text.Json;
using System.Text.Json.Nodes;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class SessionStoreManifestTests
{
    [Fact]
    public async Task Open_writes_a_v1_recording_manifest_and_the_first_metadata_entry()
    {
        using var context = new SessionStoreTestContext();

        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        Assert.True(File.Exists(context.ManifestPath));

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(context.ManifestPath));
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("Recording", root.GetProperty("status").GetString());
        Assert.Equal(context.Session.Id, root.GetProperty("id").GetString());
        Assert.Equal("streamer", root.GetProperty("channel").GetString());
        Assert.Equal("123456789", root.GetProperty("streamId").GetString());
        Assert.Equal(context.Session.StartedAt, root.GetProperty("startedAt").GetDateTimeOffset());
        Assert.False(root.TryGetProperty("endedAt", out _));
        Assert.False(root.TryGetProperty("durationSeconds", out _));
        Assert.False(root.TryGetProperty("sizeBytes", out _));

        var metadata = root.GetProperty("metadata");
        Assert.Equal(1, metadata.GetArrayLength());
        Assert.Equal("A stream", metadata[0].GetProperty("title").GetString());
        Assert.Equal("Just Chatting", metadata[0].GetProperty("game").GetString());
        Assert.Equal(SessionStoreTestContext.Now, metadata[0].GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public async Task TryLoad_round_trips_the_manifest_and_ignores_unknown_fields()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);
        await context.Store.AppendMetadataAsync(
            context.Session, context.Metadata("New title", "New game"), CancellationToken.None);

        string json = await File.ReadAllTextAsync(context.ManifestPath);
        string withFutureField = json.Replace(
            "\"version\": 1,",
            "\"version\": 1,\n  \"futureField\": { \"ignored\": true },",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(context.ManifestPath, withFutureField);

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.Version);
        Assert.Equal(SessionStatus.Recording, manifest.Status);
        Assert.Equal(context.Session.Id, manifest.Id);
        Assert.Equal(context.Session.Channel, manifest.Channel);
        Assert.Equal(context.Session.StreamId, manifest.StreamId);
        Assert.Equal(context.Session.StartedAt, manifest.StartedAt);
        Assert.Collection(
            manifest.Metadata,
            first =>
            {
                Assert.Equal("A stream", first.Title);
                Assert.Equal("Just Chatting", first.Game);
            },
            second =>
            {
                Assert.Equal("New title", second.Title);
                Assert.Equal("New game", second.Game);
            });
    }

    [Fact]
    public async Task TryLoad_returns_null_for_a_missing_or_corrupt_manifest()
    {
        using var context = new SessionStoreTestContext();

        Assert.Null(await context.Store.TryLoadAsync(context.Directory, CancellationToken.None));

        Directory.CreateDirectory(context.Directory);
        await File.WriteAllTextAsync(context.ManifestPath, "");
        Assert.Null(await context.Store.TryLoadAsync(context.Directory, CancellationToken.None));

        await File.WriteAllTextAsync(context.ManifestPath, "{ not json");
        Assert.Null(await context.Store.TryLoadAsync(context.Directory, CancellationToken.None));
    }

    [Fact]
    public async Task TryLoad_returns_null_for_an_unsupported_version_or_missing_metadata()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        var node = JsonNode.Parse(await File.ReadAllTextAsync(context.ManifestPath))!;
        node["version"] = 2;
        await File.WriteAllTextAsync(context.ManifestPath, node.ToJsonString());

        Assert.Null(await context.Store.TryLoadAsync(context.Directory, CancellationToken.None));

        node["version"] = 1;
        node["metadata"] = new JsonArray();
        await File.WriteAllTextAsync(context.ManifestPath, node.ToJsonString());

        Assert.Null(await context.Store.TryLoadAsync(context.Directory, CancellationToken.None));

        node["metadata"] = null;
        await File.WriteAllTextAsync(context.ManifestPath, node.ToJsonString());

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

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(SessionStatus.Recording, manifest.Status);
        Assert.Null(manifest.EndedAt);
        Assert.Null(manifest.DurationSeconds);
        Assert.Null(manifest.SizeBytes);
        Assert.False(File.Exists(context.IndexPath));
        Assert.Equal(2, manifest.Metadata.Count);
        Assert.Equal("Changed", manifest.Metadata[^1].Title);
        Assert.Equal("Changed game", manifest.Metadata[^1].Game);
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

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Single(manifest.Metadata);
    }

    [Fact]
    public async Task A_failed_manifest_write_leaves_the_previous_manifest_intact()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None);

        // A directory where the temp file belongs makes the write fail before the rename.
        Directory.CreateDirectory(context.ManifestPath + ".tmp");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Store.AppendMetadataAsync(
                context.Session, context.Metadata("Never written", "Never"), CancellationToken.None));

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Single(manifest.Metadata);
        Assert.Equal("A stream", manifest.Metadata[0].Title);
    }

    [Fact]
    public async Task Open_writes_one_manifest_under_concurrent_calls()
    {
        using var context = new SessionStoreTestContext();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            context.Store.OpenAsync(context.Session, context.Live, CancellationToken.None)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        var manifest = await context.Store.TryLoadAsync(context.Directory, CancellationToken.None);
        Assert.NotNull(manifest);
        var entry = Assert.Single(manifest.Metadata);
        Assert.Equal("A stream", entry.Title);
        Assert.Equal("Just Chatting", entry.Game);
        Assert.Equal(SessionStoreTestContext.Now, entry.At);
    }
}
