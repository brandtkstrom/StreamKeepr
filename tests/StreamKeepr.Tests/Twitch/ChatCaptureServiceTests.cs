using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch.Chat;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Twitch;

public sealed class ChatCaptureServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 18, 0, 0, TimeSpan.Zero);

    // Short on purpose: ticks must come from the injected clock, not from a real 1 s timer.
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(800);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly RecordingLibrary _library = new(NullLogger<RecordingLibrary>.Instance);
    private readonly FakeChatSource _source = new();
    private readonly ListLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly ChatLogStore _store;
    private readonly ChatCaptureService _service;
    private readonly RecordingSummary _recording;

    public ChatCaptureServiceTests()
    {
        var chat = Options.Create(new ChatOptions { Enabled = true });
        _store = new ChatLogStore(
            Options.Create(new StorageOptions { RecordingsRoot = _root }),
            chat,
            _clock,
            NullLogger<ChatLogStore>.Instance);
        _loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        _service = new ChatCaptureService(
            _source, _library, _store, chat, _clock, _loggers.CreateLogger<ChatCaptureService>());
        _recording = new RecordingSummary(
            "20261113T180000Z_1", "chan", "Title", "Game", Start, null,
            IsRecording: true, DurationSeconds: null, SizeBytes: 1, PartCount: 1,
            SegmentCount: 1, HasThumbnail: false, Chapters: []);
        Directory.CreateDirectory(_store.SessionDirectory(_recording));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        _loggers.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Joins_when_a_channel_starts_recording_and_finalizes_when_it_stops()
    {
        await StartRecordingAsync();
        _source.Events.Write(Message("m1", Start.AddSeconds(5)));
        await EventuallyAsync(() => File.Exists(MetaPath));

        _library.Upsert(_recording with { IsRecording = false, EndedAt = Start.AddMinutes(1) });
        await EventuallyAsync(() => _source.Parted.Contains("chan"));

        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(MetaPath));
        Assert.Equal("complete", meta.RootElement.GetProperty("status").GetString());
        Assert.Contains(Events(), evt => evt.GetProperty("kind").GetString() == "end");
    }

    [Fact]
    public async Task A_reconnect_writes_a_gap_from_the_last_message_to_the_rejoin()
    {
        await StartRecordingAsync();
        DateTimeOffset last = Start.AddSeconds(5);
        _source.Events.Write(Message("m1", last));
        _source.Events.Write(new ChatSourceEvent.Disconnected("chan", ToMs(Start.AddSeconds(30))));
        _source.Events.Write(new ChatSourceEvent.Joined("chan", ToMs(Start.AddSeconds(95))));

        JsonElement gap = await SingleGapAsync();

        Assert.Equal(ToMs(last), gap.GetProperty("from").GetInt64());
        Assert.Equal(ToMs(Start.AddSeconds(95)), gap.GetProperty("to").GetInt64());
    }

    [Fact]
    public async Task A_reconnect_before_any_message_starts_the_gap_at_the_disconnect()
    {
        await StartRecordingAsync();
        _source.Events.Write(new ChatSourceEvent.Disconnected("chan", ToMs(Start.AddSeconds(30))));
        _source.Events.Write(new ChatSourceEvent.Disconnected("chan", ToMs(Start.AddSeconds(40))));
        _source.Events.Write(new ChatSourceEvent.Joined("chan", ToMs(Start.AddSeconds(95))));

        JsonElement gap = await SingleGapAsync();

        Assert.Equal(ToMs(Start.AddSeconds(30)), gap.GetProperty("from").GetInt64());
        Assert.Equal(ToMs(Start.AddSeconds(95)), gap.GetProperty("to").GetInt64());
    }

    [Fact]
    public async Task A_failed_join_is_logged_and_retried()
    {
        _source.JoinFailures = 1;

        await StartRecordingAsync();

        Assert.Contains(_logs.Messages, message => message.Contains(
            "could not join chan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_source_is_logged_and_retried_without_faulting_the_service()
    {
        _source.ReadFailures = 1;

        await StartRecordingAsync();
        await EventuallyAsync(() => _source.ReadCalls >= 2);

        Assert.False(_service.ExecuteTask!.IsCompleted);
        Assert.Contains(_logs.Messages, message => message.Contains(
            "chat source failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shutdown_during_an_outage_closes_its_gap()
    {
        await StartRecordingAsync();
        _source.Events.Write(Message("m1", Start.AddSeconds(5)));
        _source.Events.Write(new ChatSourceEvent.Disconnected("chan", ToMs(Start.AddSeconds(30))));
        await EventuallyAsync(() => File.Exists(MetaPath));
        await Task.Delay(50);
        _clock.Advance(TimeSpan.FromMinutes(1));

        long stoppedAt = ToMs(_clock.GetUtcNow());
        await _service.StopAsync(CancellationToken.None);

        JsonElement gap = Assert.Single(Events(), evt => evt.GetProperty("kind").GetString() == "gap");
        Assert.Equal(ToMs(Start.AddSeconds(5)), gap.GetProperty("from").GetInt64());
        Assert.Equal(stoppedAt, gap.GetProperty("to").GetInt64());
    }

    private string ChatDirectory => ChatLogStore.ChatDirectory(_store.SessionDirectory(_recording));

    private string MetaPath => Path.Combine(ChatDirectory, ChatLogStore.MetaFileName);

    private async Task StartRecordingAsync()
    {
        _library.Upsert(_recording);
        _library.MarkLoaded();
        await _service.StartAsync(CancellationToken.None);
        await EventuallyAsync(() => _source.Joined.Contains("chan"));
    }

    private async Task<JsonElement> SingleGapAsync()
    {
        await EventuallyAsync(() => Events().Any(IsGap));
        return Assert.Single(Events(), IsGap);

        static bool IsGap(JsonElement evt) => evt.GetProperty("kind").GetString() == "gap";
    }

    private List<JsonElement> Events()
    {
        string path = Path.Combine(ChatDirectory, ChatLogStore.EventsFileName);
        if (!File.Exists(path))
            return [];

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    /// <summary>Advances the fake clock until the condition holds or real patience runs out.</summary>
    private async Task EventuallyAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }
    }

    private static long ToMs(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    private static ChatSourceEvent.MessageEvent Message(string id, DateTimeOffset at) =>
        new(new ChatMessage(
            id, "chan", ToMs(at), AtIsLocal: false, "42", "alice", "Alice", null,
            [], [], null, null, Action: false, "hello"));

    private sealed class FakeChatSource : IChatSource
    {
        private int _joinFailures;
        private int _readFailures;
        private int _readCalls;

        public ChatEventQueue Events { get; } = new();

        public ConcurrentQueue<string> Joined { get; } = new();

        public ConcurrentQueue<string> Parted { get; } = new();

        public int ReadFailures
        {
            get => Volatile.Read(ref _readFailures);
            set => Volatile.Write(ref _readFailures, value);
        }

        public int ReadCalls => Volatile.Read(ref _readCalls);

        public int JoinFailures
        {
            get => Volatile.Read(ref _joinFailures);
            set => Volatile.Write(ref _joinFailures, value);
        }

        public string Source => "fake";

        public ValueTask JoinAsync(string channel, CancellationToken ct)
        {
            if (Interlocked.Decrement(ref _joinFailures) >= 0)
                return ValueTask.FromException(new IOException("join refused"));

            Joined.Enqueue(channel);
            return ValueTask.CompletedTask;
        }

        public ValueTask PartAsync(string channel, CancellationToken ct)
        {
            Parted.Enqueue(channel);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<ChatSourceEvent> ReadAllAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _readCalls);
            return Interlocked.Decrement(ref _readFailures) >= 0
                ? Throw(new InvalidOperationException("socket exploded"), ct)
                : Events.ReadAllAsync(ct);
        }

        public ValueTask DisposeAsync()
        {
            Events.Complete();
            return ValueTask.CompletedTask;
        }

        private static async IAsyncEnumerable<ChatSourceEvent> Throw(
            Exception failure, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (failure is not null)
                throw failure;

            yield break;
        }
    }
}
