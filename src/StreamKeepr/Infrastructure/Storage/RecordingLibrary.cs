namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// The in-memory index of every recording. Recovery and the session store upsert summaries;
/// the UI reads <see cref="All"/> and <see cref="Find"/> without touching the disk.
/// </summary>
public sealed class RecordingLibrary(TimeProvider clock, ILogger<RecordingLibrary> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RecordingSummary> _byId = new(StringComparer.Ordinal);
    private IReadOnlyList<RecordingSummary> _all = [];
    private bool _isLoaded;

    /// <summary>Newest first by start time, with the id as the deterministic tie-breaker.</summary>
    public IReadOnlyList<RecordingSummary> All
    {
        get
        {
            lock (_gate)
                return _all;
        }
    }

    /// <summary>False until startup recovery has loaded every session, including skipped ones.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_gate)
                return _isLoaded;
        }
    }

    /// <summary>Raised after upsert, remove, and the first mark-loaded. Subscribers throttle.</summary>
    public event Action? Changed;

    public RecordingSummary? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
            return _byId.GetValueOrDefault(id);
    }

    public void Upsert(RecordingSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        lock (_gate)
        {
            _byId[summary.Id] = summary;
            Refresh();
        }

        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        bool removed;
        lock (_gate)
        {
            removed = _byId.Remove(id);
            if (removed)
                Refresh();
        }

        if (removed)
            Changed?.Invoke();
    }

    public void MarkLoaded()
    {
        bool changed;
        lock (_gate)
            changed = SetLoaded();

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>
    /// Loads every recorded session from the record store (ADR-0014); session folders without a
    /// record are never listed. A session whose media cannot be read is left out; the library
    /// still becomes loaded. The load is a single bulk update so startup pays one sort and one
    /// change notification. Ends with one Information summary of the recording count and
    /// elapsed time.
    /// </summary>
    public async Task LoadAsync(
        string recordingsRoot, SessionStore store, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsRoot);
        ArgumentNullException.ThrowIfNull(store);

        long startedAt = clock.GetTimestamp();
        string root = Path.GetFullPath(recordingsRoot);
        IReadOnlyList<RecordingSummary> summaries = await store.SummarizeAllAsync(root, ct);

        int recordingCount;
        lock (_gate)
        {
            _byId.Clear();
            foreach (var summary in summaries)
                _byId[summary.Id] = summary;

            Refresh();
            SetLoaded();
            recordingCount = _byId.Count;
        }

        logger.LogInformation(
            "Loaded {RecordingCount} recordings in {ElapsedMs:0} ms from {Root}.",
            recordingCount,
            clock.GetElapsedTime(startedAt).TotalMilliseconds,
            root);

        Changed?.Invoke();
    }

    private bool SetLoaded()
    {
        if (_isLoaded)
            return false;

        _isLoaded = true;
        return true;
    }

    private void Refresh()
    {
        _all = _byId.Values
            .OrderByDescending(summary => summary.StartedAt)
            .ThenBy(summary => summary.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
