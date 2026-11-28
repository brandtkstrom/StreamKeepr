using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;
using StreamKeepr.Tests.Records;

namespace StreamKeepr.Tests.Storage;

public sealed class ChatLogStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Pdt = Now;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly ChatTestRecords _records;
    private readonly RecordingSummary _summary = new(
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

    public ChatLogStoreTests()
    {
        Directory.CreateDirectory(_root);
        _records = new ChatTestRecords(_root);
        _records.AddSession(_summary);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Reader_maps_messages_to_the_media_timeline_and_applies_the_offset()
    {
        (ChatLogStore store, ChatReader reader) = Create(new ChatOptions());
        WritePlaylist(store.SessionDirectory(_summary));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", offsetSeconds: 3, "first"));
        store.Append("chan", Message("b", offsetSeconds: 8, "second"));
        store.CloseSession(_summary.Id, finalize: true);

        ChatQueryResult result = await QueryAsync(reader, 0, 20);

        Assert.Equal("complete", result.State);
        Assert.True(result.HasChat);
        Assert.Equal(ChatSources.Anonymous, result.Source);
        Assert.Equal([3d, 8d], result.Messages.Select(message => message.Mt));
        Assert.Equal(["first", "second"], result.Messages.Select(message => message.Text));

        ChatQueryResult shifted = await QueryAsync(reader, 0, 20, offset: 5);
        Assert.Equal([8d, 13d], shifted.Messages.Select(message => message.Mt));
    }

    [Fact]
    public async Task Messages_keep_their_badges_emotes_reply_and_action_flag()
    {
        (ChatLogStore store, ChatReader reader) = Create(new ChatOptions());
        WritePlaylist(store.SessionDirectory(_summary));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", new ChatSourceEvent.MessageEvent(new ChatMessage(
            "rich", "chan", At(4), AtIsLocal: true, "42", "alice", "Alice", "#FF0000",
            [new ChatBadge("subscriber", "12", "13")],
            [new ChatEmote("25", [new ChatEmoteRange(0, 4)])],
            Bits: 100,
            new ChatReply("p1", "bob", "Bob", "parent"),
            Action: true,
            "Kappa hi")));
        store.CloseSession(_summary.Id, finalize: false);

        ChatQueryMessage message = Assert.Single((await QueryAsync(reader, 0, 20)).Messages);

        Assert.Equal("rich", message.Id);
        Assert.Equal(At(4), message.At);
        Assert.Equal("42", message.Uid);
        Assert.Equal("#FF0000", message.Color);
        Assert.Equal(new StoredChatBadge("subscriber", "12", "13"), Assert.Single(message.Badges));
        StoredChatEmote emote = Assert.Single(message.Emotes);
        Assert.Equal("25", emote.Id);
        Assert.Equal(new StoredChatRange(0, 4), Assert.Single(emote.Ranges));
        Assert.Equal(100, message.Bits);
        Assert.Equal(new StoredChatReply("p1", "bob", "Bob", "parent"), message.Reply);
        Assert.True(message.Action);
        Assert.True(Assert.Single(_records.Messages(_summary.Id)).Local);
    }

    [Fact]
    public async Task Live_replay_reads_a_batch_once_it_is_flushed()
    {
        (ChatLogStore store, ChatReader reader) = Create(new ChatOptions());
        RecordingSummary live = _summary with { IsRecording = true, EndedAt = null, DurationSeconds = null };
        WritePlaylist(store.SessionDirectory(live));
        store.OpenSession(live, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));

        // Rows are written in batches: nothing is stored until the batch is flushed.
        Assert.Empty(_records.Messages(live.Id));
        Assert.False((await QueryAsync(reader, 0, 20, summary: live)).HasChat);

        store.Flush();

        ChatQueryResult result = await QueryAsync(reader, 0, 20, summary: live);
        Assert.Equal("recording", result.State);
        Assert.True(result.HasChat);
        Assert.Equal("a", Assert.Single(result.Messages).Id);
        Assert.Equal("recording", _records.ChatSession(live.Id)!.Status);
    }

    [Fact]
    public void A_full_batch_is_written_without_waiting_for_a_flush()
    {
        (ChatLogStore store, _) = Create(new ChatOptions());
        store.OpenSession(_summary, ChatSources.Anonymous);
        for (int i = 0; i < ChatLogStore.BatchSize; i++)
            store.Append("chan", Message($"m{i}", 3, "busy"));

        Assert.Equal(ChatLogStore.BatchSize, _records.Messages(_summary.Id).Count);
        Assert.Equal(ChatLogStore.BatchSize, _records.ChatSession(_summary.Id)!.MessageCount);
    }

    [Fact]
    public async Task Chat_survives_a_restart_and_replays_from_the_store()
    {
        (ChatLogStore store, _) = Create(new ChatOptions());
        WritePlaylist(store.SessionDirectory(_summary));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.Append("chan", Message("b", 6, "second"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(7), "b"));
        store.CloseAll(finalize: false);

        ChatTestRecords restarted = _records.Restart();
        ChatLogStore after = restarted.CreateStore(new ChatOptions(), _clock);
        ChatReader reader = ChatTestRecords.CreateReader(after, new ChatOptions());

        ChatQueryResult result = await QueryAsync(reader, 0, 20);

        Assert.True(result.HasChat);
        Assert.True(await after.HasChatAsync(_summary.Id, CancellationToken.None));
        Assert.Equal("a", Assert.Single(result.Messages).Id);
    }

    [Fact]
    public async Task A_message_stored_again_after_a_restart_replays_once()
    {
        (ChatLogStore store, ChatReader reader) = Create(new ChatOptions());
        WritePlaylist(store.SessionDirectory(_summary));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.CloseSession(_summary.Id, finalize: false);

        // A new writer has no memory of the ids the previous one stored.
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.Append("chan", Message("b", 9, "second"));
        store.CloseSession(_summary.Id, finalize: true);

        Assert.Equal(3, _records.Messages(_summary.Id).Count);
        Assert.Equal(["a", "b"], (await QueryAsync(reader, 0, 20)).Messages.Select(message => message.Id));
        Assert.Equal(
            ["a", "b"],
            (await reader.QueryTailAsync(_summary, 20, 300, 0, CancellationToken.None))
                .Messages.Select(message => message.Id));
    }

    [Fact]
    public void Events_are_stored_as_rows_with_their_fields()
    {
        (ChatLogStore store, _) = Create(new ChatOptions());
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(1), "x"));
        store.Append("chan", new ChatSourceEvent.Clear("chan", At(2), "7", "bob"));
        store.Append("chan", new ChatSourceEvent.Clear("chan", At(3), null, null));
        store.Append("chan", new ChatSourceEvent.RoomState("chan", At(4), true, false, true, 30));
        store.Append("chan", new ChatSourceEvent.Notice("chan", At(5), "msg_slowmode", "slow down"));
        store.Append("chan", new ChatSourceEvent.Gap("chan", At(6), At(5), At(6), ChatGapReasons.IrcDisconnected));
        store.Append("chan", new ChatSourceEvent.Dropped("chan", At(7), At(6), At(7), Count: 4));
        store.CloseSession(_summary.Id, finalize: true);

        List<ChatEventRow> events = _records.Events(_summary.Id);

        Assert.Equal(
            ["delete", "clear_user", "clear_all", "roomstate", "notice", "gap", "dropped", "end"],
            events.Select(row => row.Kind));
        Assert.Equal("x", events[0].TargetMessageId);
        Assert.Equal(("7", "bob"), (events[1].TargetUserId, events[1].TargetLogin));
        Assert.Equal((true, false, true, 30), (events[3].EmoteOnly, events[3].SubscribersOnly, events[3].UniqueChat, events[3].SlowSeconds));
        Assert.Equal("slow down", events[4].Reason);
        Assert.Equal((At(5), At(6), "irc-disconnected"), (events[5].FromAt, events[5].ToAt, events[5].Reason));
        Assert.Equal((4, "queue-full"), (events[6].Count, events[6].Reason));

        ChatSessionRow chat = _records.ChatSession(_summary.Id)!;
        Assert.Equal("complete", chat.Status);
        Assert.Equal(Now, chat.EndedAt);
        Assert.Equal("wss", chat.Transport);
        Assert.Equal(12, chat.LeadInSeconds);
    }

    [Fact]
    public async Task Query_hides_a_deleted_message_even_when_purge_is_off()
    {
        (ChatLogStore store, ChatReader reader) = Create(new ChatOptions());
        WritePlaylist(store.SessionDirectory(_summary));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.Append("chan", Message("b", 6, "second"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(7), "b"));
        store.CloseSession(_summary.Id, finalize: true);

        ChatQueryResult result = await QueryAsync(reader, 0, 20);

        Assert.Equal("a", Assert.Single(result.Messages).Id);
    }

    [Fact]
    public async Task Query_hides_messages_removed_by_a_user_clear_and_a_full_clear()
    {
        (ChatLogStore store, ChatReader reader) = Create(new ChatOptions());
        WritePlaylist(store.SessionDirectory(_summary));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("bob1", 2, "bob early", login: "bob", userId: "7"));
        store.Append("chan", Message("alice1", 3, "alice early"));
        store.Append("chan", new ChatSourceEvent.Clear("chan", At(4), "7", "bob"));
        store.Append("chan", Message("alice2", 5, "alice middle"));
        store.Append("chan", new ChatSourceEvent.Clear("chan", At(6), null, null));
        store.Append("chan", Message("bob2", 8, "bob after the clears", login: "bob", userId: "7"));
        store.CloseSession(_summary.Id, finalize: true);

        ChatQueryResult result = await QueryAsync(reader, 0, 20);

        Assert.Equal(["bob2"], result.Messages.Select(message => message.Id));
    }

    [Fact]
    public async Task On_finalize_purge_removes_deleted_text_and_redacts_replies()
    {
        var options = new ChatOptions { PurgeDeletedText = ChatPurgeMode.OnFinalize };
        (ChatLogStore store, ChatReader reader) = Create(options);
        WritePlaylist(store.SessionDirectory(_summary));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.Append("chan", Message("b", 6, "second"));
        store.Append("chan", Message("c", 9, "third", replyId: "b", replyText: "second"));
        store.Append(
            "chan", Message("d", 10, "fourth", replyId: "missing", replyText: "secret quote"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(11), "b"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(12), "missing"));
        store.CloseSession(_summary.Id, finalize: true);

        List<ChatMessageRow> rows = _records.Messages(_summary.Id);
        Assert.Equal(["a", "c", "d"], rows.Select(row => row.MessageId));
        Assert.All(rows, row => Assert.NotEqual("second", row.Text));
        Assert.All(rows, row => Assert.Null(row.ReplyText));
        Assert.Null(rows.Single(row => row.MessageId == "c").ReplyMessageId);

        List<ChatEventRow> events = _records.Events(_summary.Id);
        Assert.DoesNotContain(events, row => row.Kind == "delete");
        ChatEventRow purged = Assert.Single(events, row => row.Kind == "purged");
        Assert.Equal(1, purged.Count);
        Assert.Equal("1 messages, 2 replies", purged.Reason);

        ChatSessionRow chat = _records.ChatSession(_summary.Id)!;
        Assert.Equal(3, chat.MessageCount);
        Assert.Equal(1, chat.PurgedCount);
        Assert.Equal(Now, chat.PurgedAt);
        Assert.True(chat.PurgedDeletedText);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(At(10)), chat.LastMessageAt);

        ChatQueryResult result = await QueryAsync(reader, 0, 20);
        Assert.Equal(["a", "c", "d"], result.Messages.Select(message => message.Id));
        Assert.Null(result.Messages.Single(message => message.Id == "c").Reply);
        Assert.Null(result.Messages.Single(message => message.Id == "d").Reply);
    }

    [Fact]
    public void Finalizing_a_complete_session_again_writes_no_second_end_or_purge()
    {
        var options = new ChatOptions { PurgeDeletedText = ChatPurgeMode.OnFinalize };
        (ChatLogStore store, _) = Create(options);
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.Append("chan", Message("b", 6, "second"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(7), "b"));
        store.CloseSession(_summary.Id, finalize: true);

        _clock.Advance(TimeSpan.FromMinutes(5));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.CloseSession(_summary.Id, finalize: true);

        List<ChatEventRow> events = _records.Events(_summary.Id);
        Assert.Single(events, row => row.Kind == "end");
        Assert.Single(events, row => row.Kind == "purged");
        ChatSessionRow chat = _records.ChatSession(_summary.Id)!;
        Assert.Equal(Now, chat.EndedAt);
        Assert.Equal(Now, chat.PurgedAt);
        Assert.Equal(1, chat.PurgedCount);
    }

    [Fact]
    public void Turning_purge_on_does_not_purge_a_session_completed_while_it_was_off()
    {
        (ChatLogStore off, _) = Create(new ChatOptions());
        off.OpenSession(_summary, ChatSources.Anonymous);
        off.Append("chan", Message("a", 3, "first"));
        off.Append("chan", Message("b", 6, "second"));
        off.Append("chan", new ChatSourceEvent.Delete("chan", At(7), "b"));
        off.CloseSession(_summary.Id, finalize: true);

        ChatLogStore on = _records.CreateStore(
            new ChatOptions { PurgeDeletedText = ChatPurgeMode.OnFinalize }, _clock);
        on.OpenSession(_summary, ChatSources.Anonymous);
        on.CloseSession(_summary.Id, finalize: true);

        Assert.Equal(["a", "b"], _records.Messages(_summary.Id).Select(row => row.MessageId));
        List<ChatEventRow> events = _records.Events(_summary.Id);
        Assert.Contains(events, row => row.Kind == "delete");
        Assert.DoesNotContain(events, row => row.Kind == "purged");
        Assert.Null(_records.ChatSession(_summary.Id)!.PurgedAt);
    }

    [Fact]
    public void Purged_text_is_gone_from_the_database_and_its_write_ahead_log()
    {
        const string Kept = "kept-6c1e9b2d";
        const string Deleted = "deleted-4f8a0c73";
        const string Quoted = "quoted-91d5e2aa";
        var options = new ChatOptions { PurgeDeletedText = ChatPurgeMode.OnFinalize };
        (ChatLogStore store, _) = Create(options);

        // Another open connection (a reader, say) stops SQLite from checkpointing and removing
        // the log when the writer's connection closes, as can happen while the app runs.
        using var other = new Microsoft.Data.Sqlite.SqliteConnection(
            RecordStoreConnection.ConnectionString(_records.DatabasePath, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite));
        other.Open();
        using (var read = other.CreateCommand())
        {
            // SQLite attaches a connection to the log on its first read, not on open.
            read.CommandText = "SELECT count(*) FROM sqlite_master;";
            read.ExecuteScalar();
        }

        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, Kept));
        store.Append("chan", Message("b", 6, Deleted));
        store.Append("chan", Message("c", 9, "reply", replyId: "b", replyText: Quoted));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(11), "b"));
        store.CloseSession(_summary.Id, finalize: true);

        string files = DatabaseFileText();

        // The scan sees stored text, so the absences below mean the bytes were overwritten.
        Assert.Contains(Kept, files);
        Assert.DoesNotContain(Deleted, files);
        Assert.DoesNotContain(Quoted, files);
    }

    [Fact]
    public void Purge_off_keeps_deleted_text_and_its_tombstone()
    {
        (ChatLogStore store, _) = Create(new ChatOptions());
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.Append("chan", Message("c", 9, "third", replyId: "a", replyText: "first"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(11), "a"));
        store.CloseSession(_summary.Id, finalize: true);

        List<ChatMessageRow> rows = _records.Messages(_summary.Id);
        Assert.Equal(["first", "third"], rows.Select(row => row.Text));
        Assert.Equal("first", rows[1].ReplyText);
        List<ChatEventRow> events = _records.Events(_summary.Id);
        Assert.Contains(events, row => row.Kind == "delete");
        Assert.DoesNotContain(events, row => row.Kind == "purged");
        Assert.Null(_records.ChatSession(_summary.Id)!.PurgedDeletedText);
    }

    [Fact]
    public void Purge_redacts_a_quote_of_a_banned_author_whose_message_was_never_captured()
    {
        var options = new ChatOptions { PurgeDeletedText = ChatPurgeMode.OnFinalize };
        (ChatLogStore store, _) = Create(options);
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.Append(
            "chan", Message("r", 5, "reply", replyId: "before-capture", replyText: "bob secret"));
        store.Append("chan", new ChatSourceEvent.Clear("chan", At(9), "7", "bob"));
        store.CloseSession(_summary.Id, finalize: true);

        List<ChatMessageRow> rows = _records.Messages(_summary.Id);
        Assert.Equal(["a", "r"], rows.Select(row => row.MessageId));
        Assert.All(rows, row => Assert.Null(row.ReplyText));
    }

    [Fact]
    public void Purge_redacts_every_later_reply_whose_parent_was_cleared()
    {
        var options = new ChatOptions { PurgeDeletedText = ChatPurgeMode.OnFinalize };
        (ChatLogStore store, _) = Create(options);
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("p", 3, "parent text", login: "bob", userId: "7"));
        store.Append("chan", new ChatSourceEvent.Clear("chan", At(4), "7", "bob"));
        foreach (int minutes in new[] { 11, 21, 31 })
        {
            store.Append("chan", Message(
                $"r{minutes}", minutes * 60, "reply", replyId: "p", replyText: "parent text"));
        }

        store.CloseSession(_summary.Id, finalize: true);

        List<ChatMessageRow> rows = _records.Messages(_summary.Id);
        Assert.Equal(["r11", "r21", "r31"], rows.Select(row => row.MessageId));
        Assert.All(rows, row => Assert.Null(row.ReplyText));
    }

    [Fact]
    public async Task Query_hides_a_quote_of_a_deleted_message_even_when_purge_is_off()
    {
        (ChatLogStore store, ChatReader reader) = Create(new ChatOptions());
        WritePlaylist(store.SessionDirectory(_summary));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.Append("chan", Message("b", 6, "second", replyId: "a", replyText: "first"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(7), "a"));
        store.CloseSession(_summary.Id, finalize: true);

        ChatQueryResult result = await QueryAsync(reader, 0, 20);

        ChatQueryMessage reply = Assert.Single(result.Messages);
        Assert.Equal("b", reply.Id);
        Assert.Null(reply.Reply);
    }

    [Fact]
    public void Closing_a_session_logs_one_summary_with_its_message_count_and_bytes()
    {
        using var logs = new ListLoggerProvider();
        using ILoggerFactory loggers = TestLogging.Factory(logs);
        (ChatLogStore store, _) = Create(new ChatOptions(), loggers.CreateLogger<ChatLogStore>());
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 2, "first"));
        store.Append("chan", Message("b", 3, "second"));
        store.CloseSession(_summary.Id, finalize: true);

        CapturedLogEntry closed = Assert.Single(
            logs.Entries, entry => entry.Properties.ContainsKey("MessageCount"));
        Assert.Equal(LogLevel.Information, closed.Level);
        Assert.Equal(_summary.Id, closed.Properties["SessionId"]);
        Assert.Equal("chan", closed.Properties["Channel"]);
        Assert.Equal(2, closed.Properties["MessageCount"]);
        long stored = _records.ChatSession(_summary.Id)!.StoredBytes;
        Assert.True(stored > 0);
        Assert.Equal(stored, closed.Properties["Bytes"]);
        Assert.Equal(true, closed.Properties["Finalized"]);
    }

    [Fact]
    public void Appending_a_dropped_range_logs_a_warning_with_its_count_and_time_bounds()
    {
        using var logs = new ListLoggerProvider();
        using ILoggerFactory loggers = TestLogging.Factory(logs);
        (ChatLogStore store, _) = Create(new ChatOptions(), loggers.CreateLogger<ChatLogStore>());
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", new ChatSourceEvent.Dropped(
            "chan", At(6), At(3), At(6), Count: 40));

        CapturedLogEntry dropped = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(_summary.Id, dropped.Properties["SessionId"]);
        Assert.Equal("chan", dropped.Properties["Channel"]);
        Assert.Equal(40, dropped.Properties["Count"]);
        Assert.Equal(Pdt.AddSeconds(3 - 12), dropped.Properties["DroppedFrom"]);
        Assert.Equal(Pdt.AddSeconds(6 - 12), dropped.Properties["DroppedTo"]);
    }

    [Fact]
    public async Task Query_reports_messages_dropped_by_a_full_capture_queue_as_a_gap()
    {
        (ChatLogStore store, ChatReader reader) = Create(new ChatOptions());
        WritePlaylist(store.SessionDirectory(_summary));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 2, "first"));
        store.Append("chan", new ChatSourceEvent.Dropped(
            "chan", At(6), At(3), At(6), Count: 40));
        store.CloseSession(_summary.Id, finalize: true);

        ChatQueryResult result = await QueryAsync(reader, 0, 20);

        Assert.Equal([new ChatQueryGap(3, 6, 3)], result.Gaps);
    }

    [Fact]
    public async Task Query_reports_a_truncated_log_as_a_gap_to_the_end_of_the_window()
    {
        var options = new ChatOptions { MaxBytesPerSession = 1 };
        (ChatLogStore store, ChatReader reader) = Create(options);
        WritePlaylist(store.SessionDirectory(_summary));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 2, "first"));

        // The cap trips on "b", so nothing after the last stored message ("a", at 2 s) survives.
        store.Append("chan", Message("b", 13, "dropped by the cap"));
        store.CloseSession(_summary.Id, finalize: true);

        ChatQueryResult result = await QueryAsync(reader, 0, 20);

        Assert.Equal(["a"], result.Messages.Select(message => message.Id));
        ChatQueryGap gap = Assert.Single(result.Gaps);
        Assert.Equal(2, gap.From);
        Assert.Equal(20, gap.To);
        Assert.True(_records.ChatSession(_summary.Id)!.Truncated);
    }

    [Fact]
    public void A_message_that_reaches_the_cap_is_kept_and_truncates_the_log_at_once()
    {
        long size = StoredBytesOfOneMessage();
        var options = new ChatOptions { MaxBytesPerSession = size };
        (ChatLogStore store, _) = Create(options);
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 2, "first"));

        // No later message arrives: reaching the cap alone marks the log.
        store.CloseSession(_summary.Id, finalize: true);

        ChatSessionRow chat = _records.ChatSession(_summary.Id)!;
        Assert.True(chat.Truncated);
        Assert.Equal(size, chat.StoredBytes);
        Assert.Equal(["a"], _records.Messages(_summary.Id).Select(row => row.MessageId));
        ChatEventRow truncated = Assert.Single(_records.Events(_summary.Id), row => row.Kind == "truncated");
        Assert.Equal(At(2), truncated.FromAt);
        Assert.Equal(1, truncated.Count);
    }

    [Fact]
    public void A_message_one_byte_under_the_cap_leaves_the_log_open()
    {
        long size = StoredBytesOfOneMessage();
        var options = new ChatOptions { MaxBytesPerSession = size + 1 };
        (ChatLogStore store, _) = Create(options);
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 2, "first"));
        store.CloseSession(_summary.Id, finalize: true);

        Assert.False(_records.ChatSession(_summary.Id)!.Truncated);
        Assert.DoesNotContain(_records.Events(_summary.Id), row => row.Kind == "truncated");
    }

    [Fact]
    public void A_truncated_log_stores_nothing_more_for_the_session()
    {
        var options = new ChatOptions { MaxBytesPerSession = 1 };
        (ChatLogStore store, _) = Create(options);
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 2, "first"));
        store.Append("chan", Message("b", 3, "second"));
        store.Append("chan", new ChatSourceEvent.Notice("chan", At(4), "slow_on", "slow mode"));
        store.CloseSession(_summary.Id, finalize: false);

        Assert.Equal(["a"], _records.Messages(_summary.Id).Select(row => row.MessageId));
        Assert.Equal(["truncated"], _records.Events(_summary.Id).Select(row => row.Kind));
    }

    [Fact]
    public void A_reopened_session_keeps_its_byte_count_against_the_cap()
    {
        long size = StoredBytesOfOneMessage();
        var options = new ChatOptions { MaxBytesPerSession = size + size / 2 };
        (ChatLogStore store, _) = Create(options);
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 2, "first"));
        store.CloseSession(_summary.Id, finalize: false);
        Assert.False(_records.ChatSession(_summary.Id)!.Truncated);

        // A new writer for the same session counts what the first one stored.
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("b", 3, "first"));
        store.CloseSession(_summary.Id, finalize: false);

        Assert.True(_records.ChatSession(_summary.Id)!.Truncated);
        Assert.Single(_records.Events(_summary.Id), row => row.Kind == "truncated");
    }

    [Fact]
    public void A_reopened_truncated_session_stays_stopped()
    {
        var options = new ChatOptions { MaxBytesPerSession = 1 };
        (ChatLogStore store, _) = Create(options);
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 2, "first"));
        store.CloseSession(_summary.Id, finalize: false);

        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("b", 3, "second"));
        store.CloseSession(_summary.Id, finalize: false);

        Assert.Equal(["a"], _records.Messages(_summary.Id).Select(row => row.MessageId));
        Assert.Single(_records.Events(_summary.Id), row => row.Kind == "truncated");
    }

    [Fact]
    public async Task Query_reports_the_unrecorded_time_between_parts_as_a_gap()
    {
        (ChatLogStore store, ChatReader reader) = Create(new ChatOptions());
        string directory = store.SessionDirectory(_summary);
        WritePlaylist(directory);
        WritePlaylist(directory, part: 2, pdt: Pdt.AddSeconds(60 + 90));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 55, "before the outage"));
        store.Append("chan", Message("lost", 100, "while nothing was recorded"));
        store.Append("chan", Message("b", 60 + 90 + 5, "after the outage"));
        store.CloseSession(_summary.Id, finalize: true);

        ChatQueryResult result = await QueryAsync(
            reader, 50, 70, summary: _summary with { DurationSeconds = 120, PartCount = 2 });

        Assert.Equal(["a", "b"], result.Messages.Select(message => message.Id));
        Assert.Equal([new ChatQueryGap(60, 60, 90)], result.Gaps);
    }

    [Fact]
    public async Task Query_keeps_the_recorded_part_of_an_outage_that_ends_between_parts()
    {
        (ChatLogStore store, ChatReader reader) = Create(new ChatOptions());
        string directory = store.SessionDirectory(_summary);
        WritePlaylist(directory);
        WritePlaylist(directory, part: 2, pdt: Pdt.AddSeconds(60 + 90));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", new ChatSourceEvent.Gap(
            "chan", At(100), At(50), At(100), "irc-disconnected"));
        store.CloseSession(_summary.Id, finalize: true);

        ChatQueryResult result = await QueryAsync(
            reader, 40, 70, summary: _summary with { DurationSeconds = 120, PartCount = 2 });

        Assert.Contains(new ChatQueryGap(50, 60, 50), result.Gaps);
    }

    [Fact]
    public async Task Window_reports_older_and_newer_chat_outside_it()
    {
        (ChatLogStore store, ChatReader reader) = Create(new ChatOptions());
        WritePlaylist(store.SessionDirectory(_summary));
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("old", 2, "before the window"));
        store.Append("chan", Message("in", 30, "inside"));
        store.Append("chan", Message("new", 55, "after the window"));
        store.Append("chan", Message("gone", 58, "deleted after the window"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(59), "gone"));
        store.CloseSession(_summary.Id, finalize: true);

        ChatQueryResult middle = await QueryAsync(reader, 20, 40);
        Assert.Equal(["in"], middle.Messages.Select(message => message.Id));
        Assert.True(middle.HasMoreBefore);
        Assert.True(middle.HasMoreAfter);

        ChatQueryResult end = await QueryAsync(reader, 50, 60);
        Assert.Equal(["new"], end.Messages.Select(message => message.Id));
        Assert.True(end.HasMoreBefore);
        Assert.False(end.HasMoreAfter);
    }

    [Fact]
    public async Task Has_chat_is_false_before_the_first_event_and_true_after_finalize()
    {
        (ChatLogStore store, _) = Create(new ChatOptions());

        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Flush();
        Assert.False(await store.HasChatAsync(_summary.Id, CancellationToken.None));

        store.Append("chan", Message("a", 3, "first"));
        store.CloseSession(_summary.Id, finalize: true);

        Assert.True(await store.HasChatAsync(_summary.Id, CancellationToken.None));
    }

    [Fact]
    public void A_session_closed_without_chat_leaves_no_rows()
    {
        (ChatLogStore store, _) = Create(new ChatOptions());

        store.OpenSession(_summary, ChatSources.Anonymous);
        store.CloseSession(_summary.Id, finalize: true);

        Assert.Null(_records.ChatSession(_summary.Id));
        Assert.Empty(_records.Events(_summary.Id));
    }

    [Fact]
    public void A_chat_write_failure_is_logged_and_never_thrown()
    {
        using var logs = new ListLoggerProvider();
        using ILoggerFactory loggers = TestLogging.Factory(logs);
        var contexts = new FailableRecordContexts(_records.Contexts, _records.DatabasePath) { Failing = true };
        ChatLogStore store = _records.CreateStore(
            new ChatOptions { PurgeDeletedText = ChatPurgeMode.OnFinalize },
            _clock,
            loggers.CreateLogger<ChatLogStore>(),
            contexts);

        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(4), "a"));
        store.Flush();
        store.Append("chan", Message("b", 5, "second"));
        store.CloseSession(_summary.Id, finalize: true);

        Assert.Contains(logs.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("could not be stored", StringComparison.Ordinal));
        contexts.Failing = false;
        Assert.Null(_records.ChatSession(_summary.Id));
    }

    [Fact]
    public void A_truncation_whose_batch_could_not_be_stored_is_stored_with_the_next_batch()
    {
        var options = new ChatOptions { MaxBytesPerSession = 1 };
        var contexts = new FailableRecordContexts(_records.Contexts, _records.DatabasePath) { Failing = true };
        ChatLogStore store = _records.CreateStore(options, _clock, contexts: contexts);
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 2, "first"));

        contexts.Failing = false;
        store.Flush();
        store.Append("chan", Message("b", 3, "second"));
        store.CloseSession(_summary.Id, finalize: false);

        // A writer reopened later must still see the session as truncated.
        Assert.True(_records.ChatSession(_summary.Id)!.Truncated);
        Assert.Single(_records.Events(_summary.Id), row => row.Kind == "truncated");
        Assert.Empty(_records.Messages(_summary.Id));
    }

    [Fact]
    public async Task A_batch_that_could_not_be_stored_replays_as_a_gap()
    {
        var options = new ChatOptions();
        var contexts = new FailableRecordContexts(_records.Contexts, _records.DatabasePath) { Failing = true };
        ChatLogStore store = _records.CreateStore(options, _clock, contexts: contexts);
        ChatReader reader = ChatTestRecords.CreateReader(store, options);
        WritePlaylist(store.SessionDirectory(_summary));

        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "lost"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(4), "x"));
        store.Append("chan", Message("b", 5, "lost too"));
        store.Flush();
        contexts.Failing = false;
        store.Append("chan", Message("c", 8, "kept"));
        store.CloseSession(_summary.Id, finalize: true);

        ChatQueryResult result = await QueryAsync(reader, 0, 20);

        Assert.Equal(["c"], result.Messages.Select(message => message.Id));
        Assert.Equal([new ChatQueryGap(3, 5, 2)], result.Gaps);
    }

    [Fact]
    public void Messages_lost_with_a_failed_batch_do_not_count_as_stored()
    {
        using var logs = new ListLoggerProvider();
        using ILoggerFactory loggers = TestLogging.Factory(logs);
        var contexts = new FailableRecordContexts(_records.Contexts, _records.DatabasePath) { Failing = true };
        ChatLogStore store = _records.CreateStore(
            new ChatOptions(), _clock, loggers.CreateLogger<ChatLogStore>(), contexts);

        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "lost"));
        store.Append("chan", Message("b", 5, "lost too"));
        store.Flush();
        contexts.Failing = false;
        store.Append("chan", Message("c", 8, "kept"));
        store.CloseSession(_summary.Id, finalize: true);

        // The close log and the byte cap count only what reached the store.
        var closed = Assert.Single(logs.Entries, entry =>
            entry.Message.Contains("closed with", StringComparison.Ordinal));
        Assert.Equal(1, closed.Properties["MessageCount"]);
        Assert.Equal(1, _records.ChatSession(_summary.Id)!.MessageCount);
    }

    [Fact]
    public void A_batch_written_after_its_session_record_is_gone_is_logged_and_skipped()
    {
        using var logs = new ListLoggerProvider();
        using ILoggerFactory loggers = TestLogging.Factory(logs);
        (ChatLogStore store, _) = Create(new ChatOptions(), loggers.CreateLogger<ChatLogStore>());
        RecordingSummary unrecorded = _summary with { Id = "20261110T163000Z_2" };

        store.OpenSession(unrecorded, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.CloseSession(unrecorded.Id, finalize: true);

        Assert.Contains(logs.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("could not be stored", StringComparison.Ordinal));
        Assert.Null(_records.ChatSession(unrecorded.Id));
    }

    [Fact]
    public async Task Deleting_the_session_record_deletes_its_chat()
    {
        (ChatLogStore store, _) = Create(new ChatOptions());
        store.OpenSession(_summary, ChatSources.Anonymous);
        store.Append("chan", Message("a", 3, "first"));
        store.Append("chan", new ChatSourceEvent.Delete("chan", At(4), "x"));
        store.CloseSession(_summary.Id, finalize: true);

        await using (RecordStoreDbContext db = await _records.Contexts.CreateDbContextAsync())
            await db.Sessions.Where(session => session.Id == _summary.Id).ExecuteDeleteAsync();

        Assert.Null(_records.ChatSession(_summary.Id));
        Assert.Empty(_records.Messages(_summary.Id));
        Assert.Empty(_records.Events(_summary.Id));
    }

    private (ChatLogStore, ChatReader) Create(ChatOptions options, ILogger<ChatLogStore>? logger = null)
    {
        ChatLogStore store = _records.CreateStore(options, _clock, logger);
        return (store, ChatTestRecords.CreateReader(store, options));
    }

    /// <summary>
    /// What the store counts for one <c>Message("a", 2, "first")</c>, read back from an uncapped
    /// side session so the cap tests take their boundary from the store, not from its formula.
    /// </summary>
    private long StoredBytesOfOneMessage()
    {
        RecordingSummary probe = _summary with { Id = "20261110T163000Z_2" };
        _records.AddSession(probe);
        ChatLogStore store = _records.CreateStore(new ChatOptions(), _clock);
        store.OpenSession(probe, ChatSources.Anonymous);
        store.Append("chan", Message("a", 2, "first"));
        store.CloseSession(probe.Id, finalize: false);
        return _records.ChatSession(probe.Id)!.StoredBytes;
    }

    private string DatabaseFileText() => RecordStoreFiles.Text(_records.DatabasePath);

    private Task<ChatQueryResult> QueryAsync(
        ChatReader reader, double from, double to, double offset = 0, RecordingSummary? summary = null) =>
        reader.QueryAsync(summary ?? _summary, from, to, 300, offset, CancellationToken.None);

    private static void WritePlaylist(string directory, int part = 1, DateTimeOffset? pdt = null)
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

    private ChatSourceEvent.MessageEvent Message(
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
            _summary.Channel,
            At(offsetSeconds),
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

    private static long At(int offsetSeconds) =>
        Pdt.AddSeconds(offsetSeconds - 12).ToUnixTimeMilliseconds();
}
