using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Records;

namespace StreamKeepr.Tests.Storage;

/// <summary>
/// A real temp recordings root, record store, and session store for session-store tests.
/// Nothing is written under the repository. The record store lives in the root's
/// <c>.records</c> folder, so disposing the root removes it; <see cref="Records"/> can be made
/// to fail like an unwritable database.
/// </summary>
internal sealed class SessionStoreTestContext : IDisposable
{
    public static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 18, 30, 0, TimeSpan.FromHours(2));

    public static readonly DateTimeOffset Now = StartedAt.AddMinutes(5);

    public SessionStoreTestContext(
        int segmentSeconds = 6,
        ILoggerFactory? loggers = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        loggers ??= NullLoggerFactory.Instance;
        Root = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        Clock = new FakeTimeProvider(Now);
        _loggers = loggers;
        _segmentSeconds = segmentSeconds;
        _retryDelay = retryDelay ?? (static (_, _) => Task.CompletedTask);
        RecordStore = new TestRecordStore(Root);
        RecordStore.StartAsync().GetAwaiter().GetResult();
        Records = new FailableRecordContexts(RecordStore.Contexts, RecordStore.DatabasePath);
        Library = new RecordingLibrary(Clock, loggers.CreateLogger<RecordingLibrary>());
        Store = CreateStore(Library);
        Live = new LiveStream("123456789", "A stream", "Just Chatting", StartedAt, null);
        Session = Session.For(Live, "streamer", Root);
    }

    private readonly ILoggerFactory _loggers;
    private readonly int _segmentSeconds;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;

    public string Root { get; }

    public TestRecordStore RecordStore { get; }

    /// <summary>The store's context factory; set <c>Failing</c> to simulate a database write failure.</summary>
    public FailableRecordContexts Records { get; }

    public FakeTimeProvider Clock { get; }

    public SessionStore Store { get; }

    public RecordingLibrary Library { get; }

    public LiveStream Live { get; }

    public Session Session { get; }

    public string Directory => Session.Directory;

    /// <summary>
    /// A second session store and library over the same record store and recordings root, as a
    /// restarted process would build them.
    /// </summary>
    public SessionStore CreateStore(RecordingLibrary library) =>
        new(
            Options.Create(new RecorderOptions { SegmentSeconds = _segmentSeconds }),
            Records,
            library,
            Clock,
            _loggers.CreateLogger<SessionStore>(),
            _retryDelay);

    public RecordingLibrary CreateLibrary() =>
        new(Clock, _loggers.CreateLogger<RecordingLibrary>());

    public string IndexPath => Path.Combine(Directory, SessionStore.IndexFileName);

    public string PartPath(int part) => Path.Combine(Directory, $"p{part:D3}.m3u8");

    public string SegmentPath(int part, int index) =>
        Path.Combine(Directory, $"p{part:D3}_{index:D5}.m4s");

    public string InitPath(int part) => Path.Combine(Directory, $"init_{part:D3}.mp4");

    public string ThumbnailPath => Path.Combine(Directory, SessionStore.ThumbnailFileName);

    public LiveStream Metadata(string title = "A stream", string game = "Just Chatting") =>
        Live with { Title = title, Game = game };

    public void WritePart(int part, params string[][] segments)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(
            PartPath(part),
            PlaylistTestData.Playlist(6, $"init_{part:D3}.mp4", segments));
        File.WriteAllBytes(InitPath(part), [1, 2, 3]);
    }

    /// <summary>
    /// Writes a recording part playlist without ENDLIST so tests exercise the append path the
    /// way FFmpeg writes an EVENT playlist while recording.
    /// </summary>
    public void WriteLivePart(int part, params string[][] segments)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(
            PartPath(part),
            PlaylistTestData.LivePlaylist(6, $"init_{part:D3}.mp4", segments));
        File.WriteAllBytes(InitPath(part), [1, 2, 3]);
    }

    public void WriteSegment(int part, int index, int length = 10)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(SegmentPath(part, index), new byte[length]);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Root))
            System.IO.Directory.Delete(Root, recursive: true);
    }

    public static int Occurrences(string value, string token)
    {
        int count = 0;
        int index = 0;

        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
