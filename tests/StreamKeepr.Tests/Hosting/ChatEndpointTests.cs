using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Hosting;

public sealed class ChatEndpointTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly ChatLogStore _store;
    private readonly ChatReader _reader;
    private readonly RecordingLibrary _library = new(new FakeTimeProvider(), NullLogger<RecordingLibrary>.Instance);
    private readonly RecordingSummary _summary;

    public ChatEndpointTests()
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
        File.WriteAllText(
            Path.Combine(directory, "p001.m3u8"),
            "#EXTM3U\n#EXT-X-TARGETDURATION:2\n" +
            $"#EXT-X-PROGRAM-DATE-TIME:{Now:O}\n" +
            "#EXTINF:60.000000,\nsegment.m4s\n");
        _store.OpenSession(_summary, ChatSources.Anonymous);
        _store.Append("chan", new ChatSourceEvent.MessageEvent(new ChatMessage(
            "m1", "chan", Now.AddSeconds(-7).ToUnixTimeMilliseconds(), false, "42",
            "alice", "Alice", null, [], [], null, null, false, "hello")));
        _store.CloseSession(_summary.Id, finalize: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static DefaultHttpContext NewContext() => new()
    {
        RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider(),
    };

    [Fact]
    public async Task Returns_a_public_json_window()
    {
        var context = NewContext();
        context.Request.QueryString = new QueryString("?fromSec=0&toSec=60");
        context.Response.Body = new MemoryStream();

        IResult result = await ChatEndpoint.Handle(_summary.Id, context, _library, _reader);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        context.Response.Body.Position = 0;
        using JsonDocument json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.True(json.RootElement.GetProperty("hasChat").GetBoolean());
        Assert.Equal("m1", json.RootElement.GetProperty("messages")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Rejects_malformed_queries_and_unknown_recordings()
    {
        var bad = NewContext();
        bad.Request.QueryString = new QueryString("?fromSec=abc");
        await (await ChatEndpoint.Handle(_summary.Id, bad, _library, _reader))
            .ExecuteAsync(bad);
        Assert.Equal(StatusCodes.Status400BadRequest, bad.Response.StatusCode);

        var unknown = NewContext();
        unknown.Request.QueryString = new QueryString("?fromSec=0");
        await (await ChatEndpoint.Handle("missing", unknown, _library, _reader))
            .ExecuteAsync(unknown);
        Assert.Equal(StatusCodes.Status404NotFound, unknown.Response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?toSec=10")]
    [InlineData("?fromSec=-1&toSec=10")]
    [InlineData("?fromSec=10&toSec=10")]
    [InlineData("?fromSec=10&toSec=5")]
    [InlineData("?fromSec=0&toSec=601")]
    [InlineData("?fromSec=0&toSec=NaN")]
    [InlineData("?fromSec=Infinity&toSec=10")]
    [InlineData("?fromSec=0&toSec=10&limit=0")]
    [InlineData("?fromSec=0&toSec=10&limit=1001")]
    [InlineData("?fromSec=0&toSec=10&limit=ten")]
    [InlineData("?fromSec=0&toSec=10&syncOffsetSec=-31")]
    public async Task Rejects_malformed_window_bounds(string query)
    {
        var context = NewContext();
        context.Request.QueryString = new QueryString(query);

        await (await ChatEndpoint.Handle(_summary.Id, context, _library, _reader)).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }
}
