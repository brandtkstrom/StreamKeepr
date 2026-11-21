namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// What the live index endpoint should serve for one session, decided once by
/// <see cref="LiveIndex.Lookup"/> from the library and the published snapshot.
/// </summary>
public abstract record LiveIndexLookup
{
    private LiveIndexLookup()
    {
    }

    /// <summary>No such session on this channel, or a Complete session without an index.</summary>
    public sealed record NotFound : LiveIndexLookup;

    /// <summary>A transient gap: the library is not loaded, or a Recording session has no body yet.</summary>
    public sealed record Unavailable(string Reason) : LiveIndexLookup;

    /// <summary>A Complete session: serve its session index file.</summary>
    public sealed record SessionIndex(string Path) : LiveIndexLookup;

    /// <summary>A Recording session's last-known-good live index, and how old it is.</summary>
    public sealed record Live(LiveIndexSnapshot Snapshot, TimeSpan Age, bool Stale) : LiveIndexLookup;
}
