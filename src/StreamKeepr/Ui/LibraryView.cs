using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Ui;

/// <summary>
/// Scoped projection of the recording library for the UI. It merges the in-memory library,
/// DVR status, watched-channel profiles, and storage samples; coalesces source events into a
/// single <see cref="Changed"/> per 500 ms window; and reuses view-model instances so keyed
/// components can skip rendering unchanged rows.
/// </summary>
public sealed class LibraryView : ILibraryView
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(500);

    private readonly RecordingLibrary _library;
    private readonly ChannelStore _channels;
    private readonly DvrStatus _dvrStatus;
    private readonly IStorageMonitor _storage;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    private Dictionary<string, RecordingView> _recordingsById = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyList<RecordingView>> _recordingsByChannel =
        new(StringComparer.Ordinal);
    private Dictionary<string, StreamerView> _streamersByLogin = new(StringComparer.Ordinal);
    private StorageView _storageView = new(0, 0);
    private bool _loaded;
    private bool _disposed;
    private ITimer? _timer;

    public LibraryView(
        RecordingLibrary library,
        ChannelStore channels,
        DvrStatus dvrStatus,
        IStorageMonitor storage,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(dvrStatus);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(clock);

        _library = library;
        _channels = channels;
        _dvrStatus = dvrStatus;
        _storage = storage;
        _clock = clock;

        lock (_gate)
            Rebuild();

        _library.Changed += OnSourceChanged;
        _channels.Changed += OnSourceChanged;
        _dvrStatus.Changed += OnSourceChanged;
        _storage.Changed += OnSourceChanged;
    }

    public event Action? Changed;

    public bool IsLoaded => _library.IsLoaded;

    public IReadOnlyList<StreamerView> Streamers(LibraryQuery query)
    {
        var normalized = Normalize(query);

        lock (_gate)
        {
            var streamers = new List<StreamerView>(_streamersByLogin.Count);
            foreach (StreamerView streamer in _streamersByLogin.Values)
            {
                if (HasMatch(streamer, normalized))
                    streamers.Add(streamer);
            }

            streamers.Sort((left, right) => CompareStreamers(left, right, normalized.Sort));
            return streamers;
        }
    }

    public IReadOnlyList<RecordingView> Recordings(string streamerLogin, LibraryQuery query)
    {
        ArgumentNullException.ThrowIfNull(streamerLogin);

        var normalized = Normalize(query);
        streamerLogin = streamerLogin.Trim();

        lock (_gate)
        {
            if (!_recordingsByChannel.TryGetValue(streamerLogin, out var recordings) ||
                !_streamersByLogin.TryGetValue(streamerLogin, out var streamer))
            {
                return [];
            }

            var matches = new List<RecordingView>(recordings.Count);
            foreach (RecordingView recording in recordings)
            {
                if (Matches(recording, streamer, normalized))
                    matches.Add(recording);
            }

            matches.Sort((left, right) => CompareRecordings(left, right, normalized.Sort));
            return matches;
        }
    }

    public RecordingView? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        RecordingSummary? summary = _library.Find(id);
        if (summary is null)
            return null;

        lock (_gate)
        {
            RecordingView candidate = BuildRecording(
                summary, _clock.GetUtcNow(), _dvrStatus.Current);

            if (_recordingsById.TryGetValue(id, out RecordingView? existing) &&
                Equivalent(existing, candidate))
            {
                return existing;
            }

            // A deep link can arrive between a library change and the throttled rebuild.
            // Keep the cache warm so the next rebuild can reuse this instance.
            _recordingsById[id] = candidate;
            return candidate;
        }
    }

    public StorageView Storage => _storage.Current;

    public void Dispose()
    {
        ITimer? timer;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        _library.Changed -= OnSourceChanged;
        _channels.Changed -= OnSourceChanged;
        _dvrStatus.Changed -= OnSourceChanged;
        _storage.Changed -= OnSourceChanged;
        timer?.Dispose();
    }

    private void OnSourceChanged() => Schedule();

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
                return;

            _timer = _clock.CreateTimer(
                _ => Flush(),
                state: null,
                Throttle,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        ITimer? timer;
        bool changed;

        lock (_gate)
        {
            timer = _timer;
            _timer = null;

            if (_disposed)
            {
                timer?.Dispose();
                return;
            }

            changed = Rebuild();
        }

        timer?.Dispose();

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Rebuilds every cached view. Callers must hold <see cref="_gate"/>.</summary>
    private bool Rebuild()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        IReadOnlyDictionary<string, DvrSnapshot> status = _dvrStatus.Current;

        var channels = new Dictionary<string, ChannelEntry>(StringComparer.Ordinal);
        foreach (ChannelEntry channel in _channels.Snapshot)
            channels[channel.Login] = channel;

        var nextRecordings = new Dictionary<string, RecordingView>(StringComparer.Ordinal);
        var byChannel = new Dictionary<string, List<RecordingView>>(StringComparer.Ordinal);

        foreach (RecordingSummary summary in _library.All)
        {
            RecordingView candidate = BuildRecording(summary, now, status);
            if (_recordingsById.TryGetValue(summary.Id, out RecordingView? existing) &&
                Equivalent(existing, candidate))
            {
                candidate = existing;
            }

            nextRecordings[summary.Id] = candidate;
            if (!byChannel.TryGetValue(summary.Channel, out List<RecordingView>? list))
            {
                list = [];
                byChannel[summary.Channel] = list;
            }

            list.Add(candidate);
        }

        var nextByChannel = new Dictionary<string, IReadOnlyList<RecordingView>>(
            StringComparer.Ordinal);
        foreach ((string channel, List<RecordingView> list) in byChannel)
            nextByChannel[channel] = list.ToArray();

        var nextStreamers = new Dictionary<string, StreamerView>(StringComparer.Ordinal);
        foreach ((string login, IReadOnlyList<RecordingView> recordings) in nextByChannel)
        {
            StreamerView candidate = BuildStreamer(
                login, recordings, channels.GetValueOrDefault(login));

            if (_streamersByLogin.TryGetValue(login, out StreamerView? existing) &&
                existing == candidate)
            {
                candidate = existing;
            }

            nextStreamers[login] = candidate;
        }

        StorageView nextStorage = _storage.Current;
        bool loaded = _library.IsLoaded;
        bool changed = loaded != _loaded ||
            nextStorage != _storageView ||
            !SameRecordings(nextRecordings) ||
            !SameStreamers(nextStreamers);

        _recordingsById = nextRecordings;
        _recordingsByChannel = nextByChannel;
        _streamersByLogin = nextStreamers;
        _storageView = nextStorage;
        _loaded = loaded;

        return changed;
    }

    private static RecordingView BuildRecording(
        RecordingSummary summary,
        DateTimeOffset now,
        IReadOnlyDictionary<string, DvrSnapshot> status)
    {
        bool hasActiveJob = summary.IsRecording &&
            status.TryGetValue(summary.Channel, out DvrSnapshot? snapshot) &&
            snapshot.ActiveJob is { } job &&
            string.Equals(job.SessionId, summary.Id, StringComparison.Ordinal);

        RecordingState state = summary.IsRecording
            ? hasActiveJob ? RecordingState.Recording : RecordingState.Reconnecting
            : summary.PartCount > 1 ? RecordingState.Partial : RecordingState.Saved;

        double duration = summary.IsRecording
            ? Math.Max(0, (now - summary.StartedAt).TotalSeconds)
            : summary.DurationSeconds ?? 0;

        return new RecordingView(
            summary.Id,
            summary.Channel,
            summary.Title,
            string.IsNullOrWhiteSpace(summary.Game) ? null : summary.Game,
            summary.StartedAt,
            summary.IsRecording ? null : summary.EndedAt,
            double.IsFinite(duration) ? duration : 0,
            state,
            summary.SegmentCount,
            summary.PartCount,
            summary.IsRecording ? null : summary.SizeBytes,
            summary.ThumbnailUrl,
            summary.PlaylistUrl,
            // IProgressStore/F10-T2 is not on this branch. Wire the stored position here and
            // subscribe to ProgressStore.Changed when that service lands.
            ResumeSec: null,
            summary.Chapters);
    }

    private static StreamerView BuildStreamer(
        string login,
        IReadOnlyList<RecordingView> recordings,
        ChannelEntry? channel)
    {
        DateTimeOffset? lastRecordedAt = null;
        bool recording = false;

        foreach (RecordingView recordingView in recordings)
        {
            if (recordingView.State == RecordingState.Recording)
                recording = true;

            if (lastRecordedAt is null || recordingView.StartedAt > lastRecordedAt)
                lastRecordedAt = recordingView.StartedAt;
        }

        return new StreamerView(
            login,
            string.IsNullOrWhiteSpace(channel?.DisplayName) ? login : channel.DisplayName,
            channel?.AvatarUrl,
            recording,
            channel?.Paused ?? false,
            channel is not null,
            recordings.Count,
            lastRecordedAt);
    }

    private bool HasMatch(StreamerView streamer, NormalizedQuery query)
    {
        if (!_recordingsByChannel.TryGetValue(streamer.Login, out var recordings))
            return false;

        foreach (RecordingView recording in recordings)
        {
            if (Matches(recording, streamer, query))
                return true;
        }

        return false;
    }

    private static bool Matches(
        RecordingView recording,
        StreamerView streamer,
        NormalizedQuery query)
    {
        if (query.State is not null && !MatchesState(recording.State, query.State))
            return false;

        if (query.Search is null)
            return true;

        return Contains(recording.Title, query.Search) ||
            Contains(recording.Category, query.Search) ||
            Contains(streamer.Login, query.Search) ||
            Contains(streamer.DisplayName, query.Search);
    }

    private static bool Contains(string? value, string search) =>
        value is not null && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesState(RecordingState state, string filter) => filter switch
    {
        "recording" => state is RecordingState.Recording or RecordingState.Reconnecting,
        "reconnecting" => state == RecordingState.Reconnecting,
        "partial" => state == RecordingState.Partial,
        "saved" => state is RecordingState.Saved or RecordingState.Partial,
        _ => true,
    };

    private static int CompareStreamers(StreamerView left, StreamerView right, string sort)
    {
        int byRecording = right.IsRecording.CompareTo(left.IsRecording);
        if (byRecording != 0)
            return byRecording;

        int bySort = sort switch
        {
            "oldest" => CompareNullable(left.LastRecordedAt, right.LastRecordedAt),
            "az" => string.Compare(
                DisplayKey(left), DisplayKey(right), StringComparison.Ordinal),
            _ => CompareNullable(right.LastRecordedAt, left.LastRecordedAt),
        };

        return bySort != 0
            ? bySort
            : string.Compare(left.Login, right.Login, StringComparison.Ordinal);
    }

    private static int CompareRecordings(
        RecordingView left, RecordingView right, string sort)
    {
        int byStart = sort == "oldest"
            ? left.StartedAt.CompareTo(right.StartedAt)
            : right.StartedAt.CompareTo(left.StartedAt);

        return byStart != 0
            ? byStart
            : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static int CompareNullable(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null
            ? right is null ? 0 : 1
            : right is null ? -1 : left.Value.CompareTo(right.Value);

    private static string DisplayKey(StreamerView streamer) =>
        string.IsNullOrWhiteSpace(streamer.DisplayName) ? streamer.Login : streamer.DisplayName;

    private static bool Equivalent(RecordingView left, RecordingView right) =>
        left.Id == right.Id &&
        left.StreamerLogin == right.StreamerLogin &&
        left.Title == right.Title &&
        left.Category == right.Category &&
        left.StartedAt == right.StartedAt &&
        left.EndedAt == right.EndedAt &&
        left.DurationSec.Equals(right.DurationSec) &&
        left.State == right.State &&
        left.SegmentCount == right.SegmentCount &&
        left.PartCount == right.PartCount &&
        left.SizeBytes == right.SizeBytes &&
        left.ThumbnailUrl == right.ThumbnailUrl &&
        left.PlaylistUrl == right.PlaylistUrl &&
        left.ResumeSec == right.ResumeSec &&
        left.Chapters.SequenceEqual(right.Chapters);

    private bool SameRecordings(Dictionary<string, RecordingView> next)
    {
        if (next.Count != _recordingsById.Count)
            return false;

        foreach ((string id, RecordingView view) in next)
        {
            if (!_recordingsById.TryGetValue(id, out RecordingView? existing) ||
                !ReferenceEquals(existing, view))
            {
                return false;
            }
        }

        return true;
    }

    private bool SameStreamers(Dictionary<string, StreamerView> next)
    {
        if (next.Count != _streamersByLogin.Count)
            return false;

        foreach ((string login, StreamerView view) in next)
        {
            if (!_streamersByLogin.TryGetValue(login, out StreamerView? existing) ||
                !ReferenceEquals(existing, view))
            {
                return false;
            }
        }

        return true;
    }

    private static NormalizedQuery Normalize(LibraryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        string? state = query.State?.Trim().ToLowerInvariant() switch
        {
            "recording" => "recording",
            "reconnecting" => "reconnecting",
            "partial" => "partial",
            "saved" => "saved",
            _ => null,
        };

        string? search = string.IsNullOrWhiteSpace(query.Search)
            ? null
            : query.Search.Trim();

        string sort = query.Sort?.Trim().ToLowerInvariant() switch
        {
            "oldest" => "oldest",
            "az" => "az",
            _ => "newest",
        };

        return new NormalizedQuery(state, search, sort);
    }

    private readonly record struct NormalizedQuery(string? State, string? Search, string Sort);
}
