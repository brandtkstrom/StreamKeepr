using StreamKeepr.Infrastructure.Badges;

namespace StreamKeepr.Mock;

/// <summary>
/// The Mock profile's fixed badge catalogs. It replaces the Helix source so local and E2E runs
/// never call Twitch, and its image URLs use the reserved <c>.invalid</c> host so the browser never
/// calls the real CDN either. The E2E journey intercepts that host and serves the bytes. The global
/// fixture covers the badge sets the seeded chat and the running Mock chat use; the first seeded
/// channel's catalog overrides one subscriber version and adds a channel subscriber and Bits badge.
/// </summary>
internal sealed class MockBadgeSource : IBadgeSource
{
    public const string ImageHost = "https://badges.mock.invalid";

    /// <summary>The login of the first seeded channel, which has the channel fixture.</summary>
    public const string ChannelLogin = "mock_nova";

    /// <summary>The broadcaster ID of <see cref="ChannelLogin"/>.</summary>
    public const string ChannelBroadcasterId = "9100001";

    private static readonly BadgeCatalogEntry[] Fixture =
    [
        Entry("moderator", "1", "Moderator"),
        Entry("vip", "1", "VIP"),
        Entry("broadcaster", "1", "Broadcaster"),
        Entry("subscriber", "0", "Subscriber"),
        Entry("subscriber", "12", "Subscriber"),
    ];

    private static readonly BadgeCatalogEntry[] ChannelFixture =
    [
        Entry("subscriber", "0", "Nova Subscriber", scope: "channel"),
        Entry("subscriber", "3", "Nova Subscriber 3 months", scope: "channel"),
        Entry("bits", "100", "100 Bits", scope: "channel"),
    ];

    public Task<IReadOnlyList<BadgeCatalogEntry>> GetGlobalAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(Fixture);

    public Task<IReadOnlyList<BadgeCatalogEntry>> GetChannelAsync(
        string broadcasterId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BadgeCatalogEntry>>(
            broadcasterId == ChannelBroadcasterId ? ChannelFixture : []);

    private static BadgeCatalogEntry Entry(
        string set, string version, string title, string scope = "badges")
    {
        string baseUrl = $"{ImageHost}/{scope}/{set}/{version}";
        return new BadgeCatalogEntry(
            set,
            version,
            title,
            $"{baseUrl}/1",
            $"{baseUrl}/1 1x, {baseUrl}/2 2x, {baseUrl}/3 4x");
    }
}
