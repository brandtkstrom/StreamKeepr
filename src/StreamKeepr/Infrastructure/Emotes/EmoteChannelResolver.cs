using System.Collections.Concurrent;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// Resolves a Twitch login to its numeric user ID. On a cache miss the persisted channel list
/// is consulted before Helix; the result from either source (including a login Twitch does not
/// know) is cached for five minutes with single-flight so replay does not repeat lookups. The
/// cache is bounded; above the limit, expired entries and then arbitrary entries are pruned.
/// </summary>
internal sealed class EmoteChannelResolver(
    ChannelStore store,
    IStreamPoller poller,
    TimeProvider clock,
    ILogger<EmoteChannelResolver> logger) : IEmoteChannelResolver
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private const int CacheLimit = 1_024;

    private readonly ConcurrentDictionary<string, SingleFlightSlot<CachedResolution>> _cache =
        new(StringComparer.Ordinal);

    public async Task<string?> ResolveAsync(string login, CancellationToken ct)
    {
        login = TwitchLogin.Normalize(login);
        if (login.Length == 0)
            return null;

        DateTimeOffset now = clock.GetUtcNow();
        _cache.TryGetValue(login, out SingleFlightSlot<CachedResolution>? slot);
        if (slot is not null && IsFresh(slot.Value, now))
            return slot.Value!.UserId;

        if (slot is null)
        {
            BoundedEmoteCache.Trim(
                _cache,
                CacheLimit,
                now,
                CacheTtl,
                static resolution => resolution.ResolvedAt);
            slot = _cache.GetOrAdd(
                login, static _ => new SingleFlightSlot<CachedResolution>());
        }

        await slot.Gate.WaitAsync(ct);
        try
        {
            now = clock.GetUtcNow();
            if (IsFresh(slot.Value, now))
                return slot.Value!.UserId;

            // The stored channel list is authoritative; a hit is memoized like a Helix result.
            if (FindStoredId(login) is { } stored)
            {
                slot.Value = new CachedResolution(stored, clock.GetUtcNow());
                return stored;
            }

            ChannelProfile? profile;
            try
            {
                profile = await poller.GetUserAsync(login, ct);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                // A transient failure is not cached; replay still serves global emotes.
                (int? httpStatus, string reason) = EmoteFailure.Describe(exception);
                logger.LogWarning(
                    exception,
                    "Twitch user ID resolution failed for {Login}; serving global emotes only " +
                    "(HTTP {HttpStatus}): {Reason}",
                    login,
                    httpStatus,
                    reason);
                return null;
            }

            string? userId = EmoteCatalogRules.IsValidChannelId(profile?.UserId)
                ? profile!.UserId
                : null;
            slot.Value = new CachedResolution(userId, clock.GetUtcNow());
            return userId;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private static bool IsFresh(CachedResolution? cached, DateTimeOffset now) =>
        cached is not null && now - cached.ResolvedAt < CacheTtl;

    private string? FindStoredId(string login)
    {
        foreach (ChannelEntry entry in store.Snapshot)
        {
            if (string.Equals(entry.Login, login, StringComparison.OrdinalIgnoreCase) &&
                EmoteCatalogRules.IsValidChannelId(entry.UserId))
            {
                return entry.UserId;
            }
        }

        return null;
    }

    private sealed record CachedResolution(string? UserId, DateTimeOffset ResolvedAt);
}
