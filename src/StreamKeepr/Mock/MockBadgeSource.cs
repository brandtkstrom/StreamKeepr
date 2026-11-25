using StreamKeepr.Infrastructure.Badges;

namespace StreamKeepr.Mock;

/// <summary>
/// The Mock profile's fixed global badge catalog. It replaces the Helix source so local and E2E
/// runs never call Twitch, and its image URLs use the reserved <c>.invalid</c> host so the browser
/// never calls the real CDN either. The E2E journey intercepts that host and serves the bytes.
/// The fixture covers the badge sets the seeded chat and the running Mock chat use.
/// </summary>
internal sealed class MockBadgeSource : IBadgeSource
{
    public const string ImageHost = "https://badges.mock.invalid";

    private static readonly BadgeCatalogEntry[] Fixture =
    [
        Entry("moderator", "1", "Moderator"),
        Entry("vip", "1", "VIP"),
        Entry("broadcaster", "1", "Broadcaster"),
        Entry("subscriber", "0", "Subscriber"),
    ];

    public Task<IReadOnlyList<BadgeCatalogEntry>> GetGlobalAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(Fixture);

    private static BadgeCatalogEntry Entry(string set, string version, string title)
    {
        string baseUrl = $"{ImageHost}/badges/{set}/{version}";
        return new BadgeCatalogEntry(
            set,
            version,
            title,
            $"{baseUrl}/1",
            $"{baseUrl}/1 1x, {baseUrl}/2 2x, {baseUrl}/3 4x");
    }
}
