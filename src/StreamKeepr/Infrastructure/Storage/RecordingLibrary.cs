using Microsoft.Extensions.Logging;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// The in-memory index of every recording. Recovery and the session store upsert summaries;
/// the UI reads <see cref="All"/> and <see cref="Find"/> without touching the disk.
/// </summary>
public sealed class RecordingLibrary(ILogger<RecordingLibrary> logger)
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
        lock (_gate)
        {
            if (_isLoaded)
                return;

            _isLoaded = true;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Loads every session folder under the recordings root. Dot-prefixed channels and sessions
    /// are skipped, as are folders without a readable manifest. A malformed part playlist skips
    /// that one session; the library still becomes loaded. The scan is a single bulk update so
    /// startup pays one sort and one change notification.
    /// </summary>
    public async Task LoadAsync(
        string recordingsRoot, SessionStore store, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingsRoot);
        ArgumentNullException.ThrowIfNull(store);

        string root = Path.GetFullPath(recordingsRoot);
        var summaries = new List<RecordingSummary>();

        if (Directory.Exists(root))
        {
            foreach (string channelDirectory in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetFileName(channelDirectory).StartsWith('.'))
                    continue;

                foreach (string sessionDirectory in Directory.EnumerateDirectories(channelDirectory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Path.GetFileName(sessionDirectory).StartsWith('.'))
                        continue;

                    try
                    {
                        if (await store.SummarizeAsync(sessionDirectory, ct) is not { } summary)
                            continue;

                        if (!summary.IsRecording
                            && summary.PartCount == 0
                            && !File.Exists(Path.Combine(sessionDirectory, SessionStore.IndexFileName)))
                        {
                            logger.LogWarning(
                                "Skipping complete session without parts in {Directory}.",
                                sessionDirectory);
                            continue;
                        }

                        summaries.Add(summary);
                    }
                    catch (Exception ex) when (ex is FormatException or IOException
                        or InvalidDataException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(
                            ex, "Skipping unreadable session in {Directory}.", sessionDirectory);
                    }
                }
            }
        }

        lock (_gate)
        {
            _byId.Clear();
            foreach (var summary in summaries)
                _byId[summary.Id] = summary;

            Refresh();
            _isLoaded = true;
        }

        Changed?.Invoke();
    }

    private void Refresh()
    {
        _all = _byId.Values
            .OrderByDescending(summary => summary.StartedAt)
            .ThenBy(summary => summary.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
