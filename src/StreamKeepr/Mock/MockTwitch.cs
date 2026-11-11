using System.Threading.Channels;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;

namespace StreamKeepr.Mock;

/// <summary>One test-controlled EventSub push.</summary>
public sealed record MockPush(string Login, StreamObservation Observation);

/// <summary>
/// Shared state for the Mock Twitch seams. The default observations are deterministic: the
/// growing capture is live and every other mock channel is offline. Tests can override any
/// observation or enqueue a push through <see cref="SetObservation"/> and <see cref="PushAsync"/>.
/// </summary>
public sealed class MockTwitch
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StreamObservation> _observations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, MockStreamer> _streamers =
        MockCatalog.Streamers.ToDictionary(streamer => streamer.Login, StringComparer.Ordinal);
    private readonly Channel<MockPush> _pushes =
        Channel.CreateUnbounded<MockPush>(new UnboundedChannelOptions { SingleReader = true });

    public MockTwitch()
    {
        foreach (MockStreamer streamer in MockCatalog.Streamers)
            _observations[streamer.Login] = new StreamObservation.Offline();

        MockRecording growing = MockCatalog.Growing;
        _observations[growing.Channel] = new StreamObservation.Live(growing.Live);
    }

    /// <summary>Current deterministic observation for a channel; unknown logins are offline.</summary>
    public StreamObservation Observe(string login)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        login = TwitchLogin.Normalize(login);

        lock (_gate)
            return _observations.GetValueOrDefault(login) ?? new StreamObservation.Offline();
    }

    /// <summary>Test control: replace one channel's next poll observation.</summary>
    public void SetObservation(string login, StreamObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        ArgumentNullException.ThrowIfNull(observation);
        login = TwitchLogin.Normalize(login);

        lock (_gate)
            _observations[login] = observation;
    }

    /// <summary>Test control: make a channel live on its catalog stream.</summary>
    public void SetLive(string login)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        login = TwitchLogin.Normalize(login);

        MockRecording? recording = MockCatalog.Recordings
            .LastOrDefault(candidate => candidate.Channel == login);
        if (recording is null)
            throw new ArgumentException($"'{login}' is not a mock channel.", nameof(login));

        SetObservation(login, new StreamObservation.Live(recording.Live));
    }

    /// <summary>Test control: make a channel offline.</summary>
    public void SetOffline(string login)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        SetObservation(login, new StreamObservation.Offline());
    }

    /// <summary>Test control: simulate a Twitch outage for a channel.</summary>
    public void SetUnknown(string login, string error = "mock outage")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        SetObservation(login, new StreamObservation.Unknown(error));
    }

    /// <summary>Test control: enqueue an EventSub notification for the next push run.</summary>
    public ValueTask PushAsync(
        string login, StreamObservation observation, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        ArgumentNullException.ThrowIfNull(observation);

        return _pushes.Writer.WriteAsync(
            new MockPush(TwitchLogin.Normalize(login), observation), ct);
    }

    internal IAsyncEnumerable<MockPush> ReadPushesAsync(CancellationToken ct) =>
        _pushes.Reader.ReadAllAsync(ct);

    /// <summary>Resolves a catalog profile; every other login is unknown.</summary>
    public ChannelProfile? FindProfile(string login)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        login = TwitchLogin.Normalize(login);

        return _streamers.TryGetValue(login, out MockStreamer? streamer)
            ? new ChannelProfile(streamer.UserId, streamer.DisplayName, AvatarUrl: null)
            : null;
    }

    /// <summary>Title/game for the latest catalog recording owned by a broadcaster id.</summary>
    public ChannelInfo? FindChannelInfo(string broadcasterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);

        string? login = MockCatalog.Streamers
            .FirstOrDefault(streamer => streamer.UserId == broadcasterId)?.Login;
        if (login is null)
            return null;

        MockRecording recording = MockCatalog.Recordings
            .Where(candidate => candidate.Channel == login)
            .OrderByDescending(candidate => candidate.StartedAt)
            .First();

        return new ChannelInfo(recording.Title, recording.Game);
    }
}
