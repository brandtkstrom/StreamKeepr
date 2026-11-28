using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Hosting;

public sealed class ChatTailEndpointTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly ChatLogStore _store;
    private readonly ChatReader _reader;
    private readonly RecordingLibrary _library = new(new FakeTimeProvider(), NullLogger<RecordingLibrary>.Instance);
    private RecordingSummary _summary;

    public ChatTailEndpointTests()
    {
        Directory.CreateDirectory(_root);
        var records = new ChatTestRecords(_root);
        _store = records.CreateStore(new ChatOptions(), _clock);
        _reader = ChatTestRecords.CreateReader(_store, new ChatOptions());
        _summary = new RecordingSummary(
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
            Chapters: [],
            HasChat: true);
        _library.Upsert(_summary);
        records.AddSession(_summary);

        string directory = _store.SessionDirectory(_summary);
        Directory.CreateDirectory(directory);
        WritePlaylist(directory, part: 1, pdt: Now);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Busy_tail_returns_the_newest_messages_not_the_oldest()
    {
        StoreSession(("m1", 3), ("m2", 6), ("m3", 9), ("m4", 12), ("m5", 15));

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&toSec=20&limit=3");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        JsonElement root = document.RootElement;
        Assert.Equal(
            ["m3", "m4", "m5"],
            root.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("id").GetString()!).ToArray());
        Assert.True(root.GetProperty("hasMoreBefore").GetBoolean());
        Assert.False(root.GetProperty("hasMoreAfter").GetBoolean());
        Assert.Equal(9, root.GetProperty("fromSec").GetDouble());
        Assert.Equal(20, root.GetProperty("toSec").GetDouble());
    }


    [Fact]
    public async Task Sparse_tail_scans_back_past_the_old_120_second_window()
    {
        SetSummary(600);
        WritePlaylist(_store.SessionDirectory(_summary), part: 1, pdt: Now, segments: 20);
        StoreSession(("early", 3), ("late", 500));

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&toSec=500");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        JsonElement root = document.RootElement;
        Assert.Equal(
            ["early", "late"],
            root.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("id").GetString()!).ToArray());
        Assert.False(root.GetProperty("hasMoreBefore").GetBoolean());
        Assert.False(root.GetProperty("hasMoreAfter").GetBoolean());
        Assert.Equal(3, root.GetProperty("fromSec").GetDouble());
        Assert.Equal(500, root.GetProperty("toSec").GetDouble());
    }


    [Fact]
    public async Task Tail_at_zero_includes_a_message_at_media_zero()
    {
        StoreSession(("first", 0), ("later", 30));

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&toSec=0");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        JsonElement root = document.RootElement;
        JsonElement message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("first", message.GetProperty("id").GetString());
        Assert.Equal(0, message.GetProperty("mt").GetDouble());
        Assert.False(root.GetProperty("hasMoreBefore").GetBoolean());
        Assert.False(root.GetProperty("hasMoreAfter").GetBoolean());
        Assert.Equal(0, root.GetProperty("fromSec").GetDouble());
        Assert.Equal(0, root.GetProperty("toSec").GetDouble());
    }

    [Fact]
    public async Task Tail_before_the_first_message_is_empty_at_the_playhead()
    {
        StoreSession(("later", 30));

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&toSec=5");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        JsonElement root = document.RootElement;
        Assert.Empty(root.GetProperty("messages").EnumerateArray());
        Assert.False(root.GetProperty("hasMoreBefore").GetBoolean());
        Assert.False(root.GetProperty("hasMoreAfter").GetBoolean());
        Assert.Equal(5, root.GetProperty("fromSec").GetDouble());
        Assert.Equal(5, root.GetProperty("toSec").GetDouble());
    }

    [Fact]
    public async Task Tail_at_the_recording_end_clamps_the_playhead()
    {
        SetSummary(600);
        WritePlaylist(_store.SessionDirectory(_summary), part: 1, pdt: Now, segments: 20);
        StoreSession(("early", 3), ("late", 500));

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&toSec=9999");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        JsonElement root = document.RootElement;
        Assert.Equal(
            ["early", "late"],
            root.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("id").GetString()!).ToArray());
        Assert.False(root.GetProperty("hasMoreAfter").GetBoolean());
        Assert.Equal(3, root.GetProperty("fromSec").GetDouble());
        Assert.Equal(600, root.GetProperty("toSec").GetDouble());
    }


    [Fact]
    public async Task Tail_respects_the_default_and_explicit_limits()
    {
        SetSummary(900);
        WritePlaylist(_store.SessionDirectory(_summary), part: 1, pdt: Now, segments: 30);
        StoreSession(Enumerable.Range(1, 305)
            .Select(index => ($"m{index}", index))
            .ToArray());

        (HttpStatusCode defaultStatus, JsonDocument? defaultJson) =
            await GetAsync("?tail=true&toSec=305");
        Assert.Equal(HttpStatusCode.OK, defaultStatus);
        using (JsonDocument document = Assert.IsType<JsonDocument>(defaultJson))
        {
            JsonElement messages = document.RootElement.GetProperty("messages");
            Assert.Equal(300, messages.GetArrayLength());
            Assert.Equal("m6", messages[0].GetProperty("id").GetString());
            Assert.Equal("m305", messages[299].GetProperty("id").GetString());
            Assert.True(document.RootElement.GetProperty("hasMoreBefore").GetBoolean());
        }

        (HttpStatusCode oneStatus, JsonDocument? oneJson) =
            await GetAsync("?tail=true&toSec=305&limit=1");
        Assert.Equal(HttpStatusCode.OK, oneStatus);
        using (JsonDocument document = Assert.IsType<JsonDocument>(oneJson))
        {
            JsonElement message = Assert.Single(document.RootElement.GetProperty("messages").EnumerateArray());
            Assert.Equal("m305", message.GetProperty("id").GetString());
            Assert.True(document.RootElement.GetProperty("hasMoreBefore").GetBoolean());
        }

        (HttpStatusCode allStatus, JsonDocument? allJson) =
            await GetAsync("?tail=true&toSec=305&limit=1000");
        Assert.Equal(HttpStatusCode.OK, allStatus);
        using (JsonDocument document = Assert.IsType<JsonDocument>(allJson))
        {
            Assert.Equal(305, document.RootElement.GetProperty("messages").GetArrayLength());
            Assert.False(document.RootElement.GetProperty("hasMoreBefore").GetBoolean());
        }
    }

    [Theory]
    [InlineData("?tail=true&toSec=10&limit=0")]
    [InlineData("?tail=true&toSec=10&limit=1001")]
    [InlineData("?tail=true&limit=10")]
    [InlineData("?tail=true&toSec=-1")]
    [InlineData("?tail=yes&toSec=10")]
    [InlineData("?tail=true&toSec=10&syncOffsetSec=31")]
    public async Task Tail_rejects_out_of_range_queries(string query)
    {
        StoreSession(("m1", 3));

        (HttpStatusCode status, _) = await GetAsync(query);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Tail_ignores_fromSec()
    {
        StoreSession(("m1", 3), ("m2", 9));

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&fromSec=not-a-number&toSec=10");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        Assert.Equal(
            ["m1", "m2"],
            document.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("id").GetString()!).ToArray());
    }


    [Fact]
    public async Task Tail_applies_the_sync_offset_before_cutting_the_playhead()
    {
        StoreSession(("m1", 3), ("m2", 8));

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&toSec=13&limit=1&syncOffsetSec=5");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        JsonElement root = document.RootElement;
        JsonElement message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("m2", message.GetProperty("id").GetString());
        Assert.Equal(13, message.GetProperty("mt").GetDouble());
        Assert.True(root.GetProperty("hasMoreBefore").GetBoolean());
        Assert.Equal(13, root.GetProperty("fromSec").GetDouble());
    }

    [Fact]
    public async Task Tail_hides_tombstones_and_redacts_their_quotes()
    {
        _store.OpenSession(_summary, ChatSources.Anonymous);
        _store.Append("chan", Message("a", 3));
        _store.Append("chan", Message("b", 6));
        _store.Append("chan", Message("c", 9, replyId: "b", replyText: "message b"));
        _store.Append("chan", Message("d", 12));
        _store.Append("chan", new ChatSourceEvent.Delete("chan", At(13), "b"));
        _store.Append("chan", new ChatSourceEvent.Delete("chan", At(13), "d"));
        _store.CloseSession(_summary.Id, finalize: true);

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&toSec=20&limit=2");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        JsonElement root = document.RootElement;
        JsonElement[] messages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["a", "c"], messages.Select(message => message.GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("reply").ValueKind);
        Assert.False(root.GetProperty("hasMoreBefore").GetBoolean());
    }


    [Fact]
    public async Task Tail_deduplicates_a_message_stored_by_two_writers()
    {
        StoreSession(("m1", 3), ("m2", 9));

        // A later writer (after a restart) has no memory of the ids already stored.
        StoreSession(("m1", 3));

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&toSec=20");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        Assert.Equal(
            ["m1", "m2"],
            document.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("id").GetString()!).ToArray());
    }

    [Fact]
    public async Task Tail_fills_across_a_part_gap()
    {
        SetSummary(120, partCount: 2);
        WritePlaylist(
            _store.SessionDirectory(_summary), part: 2, pdt: Now.AddSeconds(150));
        StoreSession(("a", 55), ("lost", 100), ("b", 155));

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&toSec=65");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        JsonElement root = document.RootElement;
        Assert.Equal(
            ["a", "b"],
            root.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("id").GetString()!).ToArray());
        JsonElement gap = Assert.Single(root.GetProperty("gaps").EnumerateArray());
        Assert.Equal(60, gap.GetProperty("from").GetDouble());
        Assert.Equal(60, gap.GetProperty("to").GetDouble());
        Assert.Equal(90, gap.GetProperty("seconds").GetDouble());
        Assert.Equal(55, root.GetProperty("fromSec").GetDouble());
        Assert.False(root.GetProperty("hasMoreBefore").GetBoolean());
    }

    [Fact]
    public async Task Tail_in_a_part_gap_stops_at_the_playhead()
    {
        SetSummary(120, partCount: 2);
        WritePlaylist(
            _store.SessionDirectory(_summary), part: 2, pdt: Now.AddSeconds(150));
        StoreSession(("a", 55), ("b", 155));

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&toSec=60");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        JsonElement root = document.RootElement;
        JsonElement message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("a", message.GetProperty("id").GetString());
        Assert.Equal(60, root.GetProperty("toSec").GetDouble());
        Assert.Single(root.GetProperty("gaps").EnumerateArray());
    }


    [Fact]
    public async Task Tail_stops_at_the_thirty_minute_scan_cap()
    {
        SetSummary(2100);
        WritePlaylist(_store.SessionDirectory(_summary), part: 1, pdt: Now, segments: 70);
        _store.OpenSession(_summary, ChatSources.Anonymous);
        _store.Append("chan", Message("too-old", 5));
        _store.Append("chan", Message("inside", 2000));
        _store.Append("chan", new ChatSourceEvent.Delete("chan", At(2001), "too-old"));
        _store.CloseSession(_summary.Id, finalize: true);

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?tail=true&toSec=2050");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        JsonElement root = document.RootElement;
        JsonElement message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("inside", message.GetProperty("id").GetString());
        Assert.Equal(2000, root.GetProperty("fromSec").GetDouble());
        Assert.False(root.GetProperty("hasMoreBefore").GetBoolean());
    }

    [Fact]
    public async Task Non_tail_keeps_the_oldest_messages_and_its_validation()
    {
        StoreSession(("m1", 3), ("m2", 6), ("m3", 9), ("m4", 12), ("m5", 15));

        (HttpStatusCode status, JsonDocument? json) =
            await GetAsync("?fromSec=0&toSec=20&limit=3");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument document = Assert.IsType<JsonDocument>(json);
        JsonElement root = document.RootElement;
        Assert.Equal(
            ["m1", "m2", "m3"],
            root.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("id").GetString()!).ToArray());
        Assert.False(root.GetProperty("hasMoreBefore").GetBoolean());
        Assert.True(root.GetProperty("hasMoreAfter").GetBoolean());

        (HttpStatusCode tailFalseStatus, _) =
            await GetAsync("?tail=false&toSec=20");

        Assert.Equal(HttpStatusCode.BadRequest, tailFalseStatus);
    }

    private void StoreSession(params (string Id, int OffsetSeconds)[] messages)
    {
        _store.OpenSession(_summary, ChatSources.Anonymous);
        foreach ((string id, int offsetSeconds) in messages)
            _store.Append("chan", Message(id, offsetSeconds));
        _store.CloseSession(_summary.Id, finalize: true);
    }


    private void SetSummary(double durationSeconds, int partCount = 1)
    {
        _summary = _summary with
        {
            DurationSeconds = durationSeconds,
            PartCount = partCount,
        };
        _library.Upsert(_summary);
    }

    private ChatSourceEvent.MessageEvent Message(
        string id,
        int offsetSeconds,
        string? replyId = null,
        string? replyText = null) =>
        new(new ChatMessage(
            id,
            _summary.Channel,
            At(offsetSeconds),
            AtIsLocal: false,
            UserId: "42",
            Login: "alice",
            DisplayName: "Alice",
            Color: "#00FF00",
            Badges: [],
            Emotes: [],
            Bits: null,
            Reply: replyId is null
                ? null
                : new ChatReply(replyId, "bob", "Bob", replyText),
            Action: false,
            Text: $"message {id}"));

    private long At(int offsetSeconds) =>
        Now.AddSeconds(offsetSeconds - 12).ToUnixTimeMilliseconds();

    private static void WritePlaylist(
        string directory, int part, DateTimeOffset pdt, int segments = 2)
    {
        var playlist = new StringBuilder();
        playlist.Append("#EXTM3U\n");
        playlist.Append("#EXT-X-VERSION:7\n");
        playlist.Append("#EXT-X-TARGETDURATION:2\n");
        playlist.Append("#EXT-X-PLAYLIST-TYPE:EVENT\n");
        playlist.Append($"#EXT-X-PROGRAM-DATE-TIME:{pdt:O}\n");
        for (int i = 0; i < segments; i++)
            playlist.Append($"#EXTINF:30.000000,\nsegment-{i + 1}.m4s\n");

        File.WriteAllText(Path.Combine(directory, PartFiles.Playlist(part)), playlist.ToString());
    }

    private static DefaultHttpContext NewContext() => new()
    {
        RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider(),
    };

    private async Task<(HttpStatusCode Status, JsonDocument? Json)> GetAsync(string query)
    {
        var context = NewContext();
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();

        IResult result = await ChatEndpoint.Handle(_summary.Id, context, _library, _reader);
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        JsonDocument? json = context.Response.StatusCode == StatusCodes.Status200OK
            ? await JsonDocument.ParseAsync(context.Response.Body)
            : null;
        return ((HttpStatusCode)context.Response.StatusCode, json);
    }
}
