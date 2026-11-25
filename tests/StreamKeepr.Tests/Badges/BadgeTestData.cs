using StreamKeepr.Infrastructure.Badges;

namespace StreamKeepr.Tests.Badges;

internal sealed class StubBadgeSource : IBadgeSource
{
    private int _calls;

    public Func<CancellationToken, Task<IReadOnlyList<BadgeCatalogEntry>>> Global { get; set; } =
        _ => Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>([]);

    public int Calls => Volatile.Read(ref _calls);

    public Task<IReadOnlyList<BadgeCatalogEntry>> GetGlobalAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Global(ct);
    }

    public static BadgeCatalogEntry Entry(
        string set = "moderator",
        string version = "1",
        string title = "Moderator") =>
        new(
            set,
            version,
            title,
            $"https://static-cdn.jtvnw.net/badges/v1/{set}/{version}/1",
            $"https://static-cdn.jtvnw.net/badges/v1/{set}/{version}/1 1x, " +
            $"https://static-cdn.jtvnw.net/badges/v1/{set}/{version}/2 2x, " +
            $"https://static-cdn.jtvnw.net/badges/v1/{set}/{version}/3 4x");
}
