namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>One push subscription Stream Keepr wants: an event type for one broadcaster.</summary>
public sealed record ConduitSubscriptionKey(string Type, string BroadcasterUserId);

/// <summary>A subscription Twitch reports on our conduit.</summary>
public sealed record ConduitSubscription(
    string Id, string Status, string Type, string? BroadcasterUserId);

/// <summary>
/// The delta between the subscriptions on our conduit and the watched channels. Pure: the
/// conduit binding applies it through Helix.
/// </summary>
public sealed record ConduitSubscriptionPlan(
    IReadOnlySet<ConduitSubscriptionKey> Active,
    IReadOnlyList<ConduitSubscriptionKey> ToCreate,
    IReadOnlyList<string> ToDelete)
{
    public const string StreamOnline = "stream.online";
    public const string StreamOffline = "stream.offline";
    public const string Enabled = "enabled";

    private static readonly string[] Types = [StreamOnline, StreamOffline];

    /// <summary>
    /// Keeps one enabled subscription per wanted key, creates the missing keys, and deletes
    /// everything else on the conduit: unwatched channels, disabled statuses, duplicates, and
    /// types Stream Keepr does not use. The conduit is ours, so nothing on it is shared.
    /// </summary>
    public static ConduitSubscriptionPlan For(
        IEnumerable<string> watchedUserIds, IEnumerable<ConduitSubscription> existing)
    {
        List<ConduitSubscriptionKey> wanted = watchedUserIds
            .Distinct(StringComparer.Ordinal)
            .SelectMany(id => Types.Select(type => new ConduitSubscriptionKey(type, id)))
            .ToList();
        var wantedSet = wanted.ToHashSet();

        var active = new HashSet<ConduitSubscriptionKey>();
        var toDelete = new List<string>();
        foreach (ConduitSubscription subscription in existing)
        {
            ConduitSubscriptionKey? key = subscription.BroadcasterUserId is { } id
                ? new ConduitSubscriptionKey(subscription.Type, id)
                : null;

            bool keep = key is not null
                && subscription.Status == Enabled
                && wantedSet.Contains(key)
                && active.Add(key);
            if (!keep)
                toDelete.Add(subscription.Id);
        }

        return new ConduitSubscriptionPlan(
            active,
            wanted.Where(key => !active.Contains(key)).ToList(),
            toDelete);
    }

    /// <summary>A channel is covered only with both stream.online and stream.offline (contract §4).</summary>
    public static bool Covers(IReadOnlySet<ConduitSubscriptionKey> active, string broadcasterUserId) =>
        Types.All(type => active.Contains(new ConduitSubscriptionKey(type, broadcasterUserId)));
}
