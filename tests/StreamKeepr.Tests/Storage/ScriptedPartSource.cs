using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

/// <summary>
/// The one test double at the live index's part-read seam. It records every read, can serve
/// in-memory content, fail a path, or hold reads until released; anything else is read from
/// disk through <see cref="PartPlaylistReader"/> like production.
/// </summary>
internal sealed class ScriptedPartSource : IPartPlaylistSource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _contents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _errors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
    private readonly List<string> _reads = [];
    private bool _blockAll;

    /// <summary>Completes when a held read has started.</summary>
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completing it lets every held read continue.</summary>
    public TaskCompletionSource Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string> Reads
    {
        get
        {
            lock (_gate)
                return _reads.ToArray();
        }
    }

    /// <summary>Serves <paramref name="content"/> for <paramref name="path"/> instead of the disk.</summary>
    public ScriptedPartSource Add(string path, string content)
    {
        lock (_gate)
            _contents[path] = content;
        return this;
    }

    public void ThrowOn(string path, Exception error)
    {
        lock (_gate)
            _errors[path] = error;
    }

    public void StopThrowing(string path)
    {
        lock (_gate)
            _errors.Remove(path);
    }

    /// <summary>Holds reads of <paramref name="path"/> until <see cref="Release"/> completes.</summary>
    public void BlockOn(string path)
    {
        lock (_gate)
            _blocked.Add(path);
    }

    /// <summary>Holds every read until <see cref="Release"/> completes.</summary>
    public void BlockAll()
    {
        lock (_gate)
            _blockAll = true;
    }

    public void Reset()
    {
        lock (_gate)
            _reads.Clear();
    }

    public async Task<string> ReadAsync(string path, CancellationToken ct)
    {
        bool block;
        Exception? error;
        string? content;
        lock (_gate)
        {
            _reads.Add(path);
            block = _blockAll || _blocked.Contains(path);
            _errors.TryGetValue(path, out error);
            _contents.TryGetValue(path, out content);
        }

        if (block)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }

        if (error is not null)
            throw error;

        return content ?? await PartPlaylistReader.ReadAllTextAsync(path, ct);
    }
}
