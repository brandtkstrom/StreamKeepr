using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Mock;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Mock;

public sealed class MockChatSeederTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Seeds_chat_rows_for_finished_recordings_once()
    {
        Directory.CreateDirectory(_root);
        var records = new ChatTestRecords(_root);
        var library = new RecordingLibrary(new FakeTimeProvider(Start), NullLogger<RecordingLibrary>.Instance);
        RecordingSummary finished = Summary("20261110T163000Z_1", isRecording: false);
        RecordingSummary live = Summary("20261110T163000Z_2", isRecording: true);
        foreach (RecordingSummary summary in new[] { finished, live })
        {
            library.Upsert(summary);
            records.AddSession(summary);
        }

        var options = new ChatOptions();
        ChatLogStore store = records.CreateStore(options, new FakeTimeProvider(Start));
        WritePlaylist(store.SessionDirectory(finished));
        WritePlaylist(store.SessionDirectory(live));
        var seeder = new MockChatSeeder(
            library, store, Options.Create(options), NullLogger<MockChatSeeder>.Instance);

        await seeder.StartAsync(CancellationToken.None);
        await seeder.StartAsync(CancellationToken.None);

        Assert.Equal(ChatSources.Mock, records.ChatSession(finished.Id)!.Source);
        Assert.Equal("complete", records.ChatSession(finished.Id)!.Status);
        Assert.Equal(5, records.Messages(finished.Id).Count);
        Assert.Null(records.ChatSession(live.Id));

        ChatQueryResult replay = await ChatTestRecords.CreateReader(store, options)
            .QueryAsync(finished, 0, 60, 300, 0, CancellationToken.None);
        Assert.True(replay.HasChat);
        Assert.Equal("seed-emotes", replay.Messages[0].Id);
    }

    private static RecordingSummary Summary(string id, bool isRecording) => new(
        id,
        "chan",
        "Title",
        "Game",
        Start,
        isRecording ? null : Start.AddMinutes(1),
        IsRecording: isRecording,
        DurationSeconds: isRecording ? null : 60,
        SizeBytes: 1,
        PartCount: 1,
        SegmentCount: 2,
        HasThumbnail: false,
        Chapters: []);

    private static void WritePlaylist(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, PartFiles.Playlist(1)),
            "#EXTM3U\n#EXT-X-TARGETDURATION:2\n" +
            $"#EXT-X-PROGRAM-DATE-TIME:{Start:O}\n" +
            "#EXTINF:60.000000,\nsegment.m4s\n");
    }
}
