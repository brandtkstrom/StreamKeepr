using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

public sealed class ConduitSubscriptionPlanTests
{
    private const string Online = ConduitSubscriptionPlan.StreamOnline;
    private const string Offline = ConduitSubscriptionPlan.StreamOffline;

    [Fact]
    public void Empty_conduit_creates_both_events_for_every_watched_channel()
    {
        var plan = ConduitSubscriptionPlan.For(["1", "2"], []);

        Assert.Equal(
            [Key(Online, "1"), Key(Offline, "1"), Key(Online, "2"), Key(Offline, "2")],
            plan.ToCreate);
        Assert.Empty(plan.ToDelete);
        Assert.Empty(plan.Active);
    }

    [Fact]
    public void Enabled_matching_subscriptions_are_kept()
    {
        var plan = ConduitSubscriptionPlan.For(
            ["1"],
            [Sub("a", Online, "1"), Sub("b", Offline, "1")]);

        Assert.Empty(plan.ToCreate);
        Assert.Empty(plan.ToDelete);
        Assert.True(plan.Active.SetEquals([Key(Online, "1"), Key(Offline, "1")]));
    }

    [Fact]
    public void Only_the_missing_event_is_created()
    {
        var plan = ConduitSubscriptionPlan.For(["1"], [Sub("a", Online, "1")]);

        Assert.Equal([Key(Offline, "1")], plan.ToCreate);
        Assert.Empty(plan.ToDelete);
    }

    [Fact]
    public void Subscriptions_for_unwatched_channels_are_deleted()
    {
        var plan = ConduitSubscriptionPlan.For(
            ["1"],
            [Sub("a", Online, "1"), Sub("b", Offline, "1"), Sub("c", Online, "9"), Sub("d", Offline, "9")]);

        Assert.Equal(["c", "d"], plan.ToDelete);
        Assert.Empty(plan.ToCreate);
    }

    [Fact]
    public void Disabled_subscriptions_are_deleted_and_recreated()
    {
        var plan = ConduitSubscriptionPlan.For(
            ["1"],
            [Sub("a", Online, "1", "authorization_revoked"), Sub("b", Offline, "1")]);

        Assert.Equal(["a"], plan.ToDelete);
        Assert.Equal([Key(Online, "1")], plan.ToCreate);
        Assert.True(plan.Active.SetEquals([Key(Offline, "1")]));
    }

    [Fact]
    public void Duplicates_and_foreign_types_are_deleted()
    {
        var plan = ConduitSubscriptionPlan.For(
            ["1"],
            [
                Sub("a", Online, "1"),
                Sub("b", Online, "1"),
                Sub("c", Offline, "1"),
                Sub("d", "channel.update", "1"),
                new ConduitSubscription("e", "enabled", "conduit.shard.disabled", null),
            ]);

        Assert.Equal(["b", "d", "e"], plan.ToDelete);
        Assert.Empty(plan.ToCreate);
    }

    [Fact]
    public void Duplicate_watched_ids_are_planned_once()
    {
        var plan = ConduitSubscriptionPlan.For(["1", "1"], []);

        Assert.Equal([Key(Online, "1"), Key(Offline, "1")], plan.ToCreate);
    }

    [Fact]
    public void Covers_requires_both_events()
    {
        var active = new HashSet<ConduitSubscriptionKey> { Key(Online, "1"), Key(Online, "2"), Key(Offline, "2") };

        Assert.False(ConduitSubscriptionPlan.Covers(active, "1"));
        Assert.True(ConduitSubscriptionPlan.Covers(active, "2"));
        Assert.False(ConduitSubscriptionPlan.Covers(active, "3"));
    }

    private static ConduitSubscriptionKey Key(string type, string userId) => new(type, userId);

    private static ConduitSubscription Sub(
        string id, string type, string userId, string status = "enabled") =>
        new(id, status, type, userId);
}
