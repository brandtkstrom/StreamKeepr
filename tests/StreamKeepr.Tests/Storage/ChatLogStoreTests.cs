using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch.Chat;

namespace StreamKeepr.Tests.Storage;

public sealed class ChatLogStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Pdt = Now;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Now);

    public ChatLogStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Reader_maps_messages_to_the_media_timeline_and_applies_the_offset()
    {
        (ChatLogStore store, ChatReader reader, RecordingSummary summary) = Create(
            new ChatOptions());
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", Message(summary, "a", offsetSeconds: 3, "first"));
        store.Append("chan", Message(summary, "b", offsetSeconds: 8, "second"));
        store.CloseSession(summary.Id, finalize: true);

        ChatQueryResult result = reader.Query(summary, 0, 20, 300, 0);

        Assert.Equal("complete", result.State);
        Assert.True(result.HasChat);
        Assert.Equal([3d, 8d], result.Messages.Select(message => message.Mt));
        Assert.Equal(["first", "second"], result.Messages.Select(message => message.Text));

        ChatQueryResult shifted = reader.Query(summary, 0, 20, 300, 5);
        Assert.Equal([8d, 13d], shifted.Messages.Select(message => message.Mt));
    }

    [Fact]
    public void Query_hides_a_deleted_message_even_when_purge_is_off()
    {
        (ChatLogStore store, ChatReader reader, RecordingSummary summary) = Create(
            new ChatOptions());
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", Message(summary, "a", 3, "first"));
        store.Append("chan", Message(summary, "b", 6, "second"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(summary, 7), "b"));
        store.CloseSession(summary.Id, finalize: true);

        ChatQueryResult result = reader.Query(summary, 0, 20, 300, 0);

        Assert.Equal("a", Assert.Single(result.Messages).Id);
    }

    [Fact]
    public void On_finalize_purge_removes_deleted_text_and_redacts_replies()
    {
        var options = new ChatOptions { PurgeDeletedText = ChatPurgeMode.OnFinalize };
        (ChatLogStore store, ChatReader reader, RecordingSummary summary) = Create(options);
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", Message(summary, "a", 3, "first"));
        store.Append("chan", Message(summary, "b", 6, "second"));
        store.Append("chan", Message(summary, "c", 9, "third", replyId: "b", replyText: "second"));
        store.Append(
            "chan", Message(summary, "d", 10, "fourth", replyId: "missing", replyText: "secret quote"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(summary, 11), "b"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(summary, 12), "missing"));
        store.CloseSession(summary.Id, finalize: true);

        string chatDirectory = ChatLogLayout.ChatDirectory(directory);
        string bucket = Assert.Single(
            Directory.GetFiles(chatDirectory, "*.jsonl"),
            path => !path.EndsWith("events.jsonl", StringComparison.Ordinal));
        string raw = File.ReadAllText(bucket);
        Assert.DoesNotContain("second", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secret quote", raw, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"a\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"c\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"d\"", raw, StringComparison.Ordinal);

        string events = File.ReadAllText(Path.Combine(chatDirectory, "events.jsonl"));
        Assert.Contains("purged", events, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\":\"delete\"", events, StringComparison.Ordinal);

        ChatQueryResult result = reader.Query(summary, 0, 20, 300, 0);
        Assert.Equal(["a", "c", "d"], result.Messages.Select(message => message.Id));
        Assert.Null(result.Messages.Single(message => message.Id == "c").Reply);
        Assert.Null(result.Messages.Single(message => message.Id == "d").Reply);
    }

    [Fact]
    public void Purge_redacts_a_quote_of_a_banned_author_whose_message_was_never_captured()
    {
        var options = new ChatOptions { PurgeDeletedText = ChatPurgeMode.OnFinalize };
        (ChatLogStore store, _, RecordingSummary summary) = Create(options);
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", Message(summary, "a", 3, "first"));
        store.Append(
            "chan", Message(summary, "r", 5, "reply", replyId: "before-capture", replyText: "bob secret"));
        store.Append("chan", new ChatSourceEvent.Clear("chan", At(summary, 9), "7", "bob"));
        store.CloseSession(summary.Id, finalize: true);

        string raw = string.Concat(MessageBuckets(directory).Select(File.ReadAllText));
        Assert.DoesNotContain("bob secret", raw, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"r\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Purge_redacts_a_reply_in_a_later_bucket_whose_parent_was_cleared()
    {
        var options = new ChatOptions { PurgeDeletedText = ChatPurgeMode.OnFinalize };
        (ChatLogStore store, _, RecordingSummary summary) = Create(options);
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        store.OpenSession(summary, ChatSources.Anonymous);

        // Parent and ban in the first 10-minute bucket; replies in the next three buckets.
        store.Append("chan", Message(summary, "p", 3, "parent text", login: "bob", userId: "7"));
        store.Append("chan", new ChatSourceEvent.Clear("chan", At(summary, 4), "7", "bob"));
        foreach (int minutes in new[] { 11, 21, 31 })
        {
            store.Append("chan", Message(
                summary, $"r{minutes}", minutes * 60, "reply", replyId: "p", replyText: "parent text"));
        }

        store.CloseSession(summary.Id, finalize: true);

        string[] buckets = MessageBuckets(directory);
        Assert.Equal(3, buckets.Length);
        foreach (string bucket in buckets)
            Assert.DoesNotContain("parent text", File.ReadAllText(bucket), StringComparison.Ordinal);
    }

    [Fact]
    public void Query_hides_a_quote_of_a_deleted_message_even_when_purge_is_off()
    {
        (ChatLogStore store, ChatReader reader, RecordingSummary summary) = Create(
            new ChatOptions());
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", Message(summary, "a", 3, "first"));
        store.Append("chan", Message(summary, "b", 6, "second", replyId: "a", replyText: "first"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(summary, 7), "a"));
        store.CloseSession(summary.Id, finalize: true);

        ChatQueryResult result = reader.Query(summary, 0, 20, 300, 0);

        ChatQueryMessage reply = Assert.Single(result.Messages);
        Assert.Equal("b", reply.Id);
        Assert.Null(reply.Reply);
    }

    [Fact]
    public void Query_reports_messages_dropped_by_a_full_capture_queue_as_a_gap()
    {
        (ChatLogStore store, ChatReader reader, RecordingSummary summary) = Create(
            new ChatOptions());
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", Message(summary, "a", 2, "first"));
        store.Append("chan", new ChatSourceEvent.Dropped(
            "chan", At(summary, 6), At(summary, 3), At(summary, 6), Count: 40));
        store.CloseSession(summary.Id, finalize: true);

        ChatQueryResult result = reader.Query(summary, 0, 20, 300, 0);

        Assert.Equal([new ChatQueryGap(3, 6, 3)], result.Gaps);
    }

    [Fact]
    public void Query_reports_a_truncated_log_as_a_gap_to_the_end_of_the_window()
    {
        var options = new ChatOptions { MaxBytesPerSession = 1 };
        (ChatLogStore store, ChatReader reader, RecordingSummary summary) = Create(options);
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", Message(summary, "a", 2, "first"));

        // The cap trips on "b", so nothing after the last stored message ("a", at 2 s) survives.
        store.Append("chan", Message(summary, "b", 13, "dropped by the cap"));
        store.CloseSession(summary.Id, finalize: true);

        ChatQueryResult result = reader.Query(summary, 0, 20, 300, 0);

        Assert.Equal(["a"], result.Messages.Select(message => message.Id));
        ChatQueryGap gap = Assert.Single(result.Gaps);
        Assert.Equal(2, gap.From);
        Assert.Equal(20, gap.To);
    }

    [Fact]
    public void Query_reports_the_unrecorded_time_between_parts_as_a_gap()
    {
        (ChatLogStore store, ChatReader reader, RecordingSummary summary) = Create(
            new ChatOptions());
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        WritePlaylist(directory, part: 2, pdt: Pdt.AddSeconds(60 + 90));
        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", Message(summary, "a", 55, "before the outage"));
        store.Append("chan", Message(summary, "lost", 100, "while nothing was recorded"));
        store.Append("chan", Message(summary, "b", 60 + 90 + 5, "after the outage"));
        store.CloseSession(summary.Id, finalize: true);

        ChatQueryResult result = reader.Query(
            summary with { DurationSeconds = 120, PartCount = 2 }, 50, 70, 300, 0);

        Assert.Equal(["a", "b"], result.Messages.Select(message => message.Id));
        Assert.Equal([new ChatQueryGap(60, 60, 90)], result.Gaps);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(13)]
    public void Reader_finds_messages_for_a_bucket_size_that_does_not_divide_an_hour(int minutes)
    {
        (ChatLogStore store, ChatReader reader, RecordingSummary summary) = Create(
            new ChatOptions { BucketMinutes = minutes });
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", Message(summary, "a", 3, "first"));
        store.Append("chan", Message(summary, "b", 50, "second"));
        store.CloseSession(summary.Id, finalize: true);

        ChatQueryResult result = reader.Query(summary, 0, 60, 300, 0);

        Assert.Equal(["a", "b"], result.Messages.Select(message => message.Id));
    }

    [Fact]
    public void Query_keeps_the_recorded_part_of_an_outage_that_ends_between_parts()
    {
        (ChatLogStore store, ChatReader reader, RecordingSummary summary) = Create(
            new ChatOptions());
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        WritePlaylist(directory, part: 2, pdt: Pdt.AddSeconds(60 + 90));
        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", new ChatSourceEvent.Gap(
            "chan", At(summary, 100), At(summary, 50), At(summary, 100), "irc-disconnected"));
        store.CloseSession(summary.Id, finalize: true);

        ChatQueryResult result = reader.Query(
            summary with { DurationSeconds = 120, PartCount = 2 }, 40, 70, 300, 0);

        Assert.Contains(new ChatQueryGap(50, 60, 50), result.Gaps);
    }

    [Fact]
    public void Purge_leaves_a_bucket_with_an_unreadable_row_untouched()
    {
        var options = new ChatOptions { PurgeDeletedText = ChatPurgeMode.OnFinalize };
        (ChatLogStore store, _, RecordingSummary summary) = Create(options);
        string directory = store.SessionDirectory(summary);
        WritePlaylist(directory);
        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", Message(summary, "a", 3, "first"));
        store.Append("chan", Message(summary, "b", 6, "second"));
        File.AppendAllText(MessageBuckets(directory).Single(), "{not json\n");
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(summary, 7), "b"));
        store.CloseSession(summary.Id, finalize: true);

        string raw = File.ReadAllText(MessageBuckets(directory).Single());
        Assert.Contains("{not json", raw, StringComparison.Ordinal);
        Assert.Contains("second", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Has_chat_is_false_before_the_first_event_and_true_after_finalize()
    {
        var options = new ChatOptions();
        (ChatLogStore store, _, RecordingSummary summary) = Create(options);
        string directory = store.SessionDirectory(summary);

        Assert.False(ChatLogLayout.HasChat(directory, NullLogger.Instance));

        store.OpenSession(summary, ChatSources.Anonymous);
        store.Append("chan", Message(summary, "a", 3, "first"));
        store.CloseSession(summary.Id, finalize: true);

        Assert.True(ChatLogLayout.HasChat(directory, NullLogger.Instance));
    }

    private (ChatLogStore, ChatReader, RecordingSummary) Create(ChatOptions options)
    {
        var storage = Options.Create(new StorageOptions { RecordingsRoot = _root });
        var chat = Options.Create(options);
        var store = new ChatLogStore(
            storage, chat, _clock, NullLogger<ChatLogStore>.Instance);
        var reader = new ChatReader(store, chat, NullLogger<ChatReader>.Instance);
        var summary = new RecordingSummary(
            "20261110T163000Z_1",
            "chan",
            "Title",
            "Game",
            Now,
            Now.AddMinutes(1),
            IsRecording: false,
            DurationSeconds: 60,
            SizeBytes: 1,
            PartCount: 1,
            SegmentCount: 2,
            HasThumbnail: false,
            Chapters: []);
        return (store, reader, summary);
    }

    private void WritePlaylist(string directory, int part = 1, DateTimeOffset? pdt = null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, PartFiles.Playlist(part)),
            "#EXTM3U\n" +
            "#EXT-X-VERSION:7\n" +
            "#EXT-X-TARGETDURATION:2\n" +
            "#EXT-X-PLAYLIST-TYPE:EVENT\n" +
            $"#EXT-X-PROGRAM-DATE-TIME:{pdt ?? Pdt:O}\n" +
            "#EXTINF:30.000000,\nsegment-1.m4s\n" +
            "#EXTINF:30.000000,\nsegment-2.m4s\n");
    }

    private static ChatSourceEvent.MessageEvent Message(
        RecordingSummary summary,
        string id,
        int offsetSeconds,
        string text,
        string? replyId = null,
        string? replyText = null,
        string login = "alice",
        string userId = "42")
    {
        var message = new ChatMessage(
            id,
            summary.Channel,
            At(summary, offsetSeconds),
            AtIsLocal: false,
            UserId: userId,
            Login: login,
            DisplayName: login,
            Color: "#00FF00",
            Badges: [],
            Emotes: [],
            Bits: null,
            Reply: replyId is null
                ? null
                : new ChatReply(replyId, "bob", "Bob", replyText),
            Action: false,
            Text: text);
        return new ChatSourceEvent.MessageEvent(message);
    }

    private static string[] MessageBuckets(string sessionDirectory) =>
        Directory.GetFiles(ChatLogLayout.ChatDirectory(sessionDirectory), "*.jsonl")
            .Where(path => !path.EndsWith("events.jsonl", StringComparison.Ordinal))
            .ToArray();

    private static long At(RecordingSummary summary, int offsetSeconds) =>
        Pdt.AddSeconds(offsetSeconds - 12).ToUnixTimeMilliseconds();
}
