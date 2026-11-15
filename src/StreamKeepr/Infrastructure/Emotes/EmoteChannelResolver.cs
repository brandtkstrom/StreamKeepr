using System.Collections.Concurrent;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// Resolves a Twitch login to its numeric user ID. The persisted channel list is authoritative
/// and is checked on every call; the Helix fallback result, including a login Twitch does not
/// know, is cached for five minutes with single-flight so replay does not repeat lookups.
/// </summary>
internal sealed class EmoteChannelResolver(
    ChannelStore store,
    IStreamPoller poller,
    TimeProvider clock)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, CacheSlot> _cache =
        new(StringComparer.Ordinal);

    public async Task<string?> ResolveAsync(string login, CancellationToken ct)
    {
        login = TwitchLogin.Normalize(login);
        if (login.Length == 0)
            return null;

        if (FindStoredId(login) is { } stored)
            return stored;

        CacheSlot slot = _cache.GetOrAdd(login, _ => new CacheSlot());
        DateTimeOffset now = clock.GetUtcNow();
        if (slot.Cached is { } cached && now - cached.ResolvedAt < CacheTtl)
            return cached.UserId;

        await slot.Gate.WaitAsync(ct);
        try
        {
            now = clock.GetUtcNow();
            if (slot.Cached is { } refreshed && now - refreshed.ResolvedAt < CacheTtl)
                return refreshed.UserId;

            ChannelProfile? profile;
            try
            {
                profile = await poller.GetUserAsync(login, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Unresolved for this request; a transient failure is not cached.
                return null;
            }

            string? userId = EmoteCatalogRules.IsValidChannelId(profile?.UserId)
                ? profile!.UserId
                : null;
            slot.Cached = new CachedResolution(userId, clock.GetUtcNow());
            return userId;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

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

    private sealed class CacheSlot
    {
        private CachedResolution? _cached;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public CachedResolution? Cached
        {
            get => Volatile.Read(ref _cached);
            set => Volatile.Write(ref _cached, value);
        }
    }

    private sealed record CachedResolution(string? UserId, DateTimeOffset ResolvedAt);
}
