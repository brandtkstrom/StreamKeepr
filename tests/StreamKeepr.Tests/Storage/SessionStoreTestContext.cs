using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

/// <summary>
/// A real temp recordings root and store for session-store tests. Nothing is written
/// under the repository.
/// </summary>
internal sealed class SessionStoreTestContext : IDisposable
{
    public static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 18, 30, 0, TimeSpan.FromHours(2));

    public static readonly DateTimeOffset Now = StartedAt.AddMinutes(5);

    public SessionStoreTestContext(int segmentSeconds = 6)
    {
        Root = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        Clock = new FakeTimeProvider(Now);
        Library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);
        Store = new SessionStore(
            Options.Create(new RecorderOptions { SegmentSeconds = segmentSeconds }),
            Library,
            Clock,
            NullLogger<SessionStore>.Instance);
        Live = new LiveStream("123456789", "A stream", "Just Chatting", StartedAt, null);
        Session = Session.For(Live, "streamer", Root);
    }

    public string Root { get; }

    public FakeTimeProvider Clock { get; }

    public SessionStore Store { get; }

    public RecordingLibrary Library { get; }

    public LiveStream Live { get; }

    public Session Session { get; }

    public string Directory => Session.Directory;

    public string ManifestPath => Path.Combine(Directory, SessionStore.ManifestFileName);

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
