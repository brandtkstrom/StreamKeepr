using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// App-token conduit transport (ADR-0007): one conduit with one WebSocket shard. Subscriptions
/// live on the conduit and survive socket replacement, so each session only binds shard 0 to
/// its WebSocket and reconciles the delta against the watched channels. Admin sign-in and
/// logout have no effect on it.
/// </summary>
public sealed class ConduitEventSubBinding(
    EventSubConduitApi api,
    IConduitStateStore stateStore,
    ChannelStore channels,
    PollSchedule schedule,
    IOptions<TwitchOptions> options,
    ILogger<ConduitEventSubBinding> logger) : IEventSubBinding
{
    // The conduit this process is using. Kept in memory too, so a failed state save does not
    // make the next session create (and leak) another conduit.
    private string? _conduitId;

    public string Transport => nameof(EventSubTransportMode.Conduit);

    /// <summary>Never raised: the app token does not depend on admin sign-in.</summary>
    public event Action? RestartRequested
    {
        add { }
        remove { }
    }

    public async Task<IEventSubSessionBinding?> BeginSessionAsync(CancellationToken ct)
    {
        string conduitId = await EnsureConduitAsync(ct);
        return new Session(this, conduitId);
    }

    /// <summary>
    /// Reuses our conduit while Twitch still has it; otherwise creates and saves a new one.
    /// Never deletes a conduit it did not create: another instance may share the client ID.
    /// </summary>
    private async Task<string> EnsureConduitAsync(CancellationToken ct)
    {
        string clientId = options.Value.ClientId;
        ConduitState? saved = await stateStore.LoadAsync(ct);
        if (saved is not null && saved.ClientId != clientId && _conduitId is null)
        {
            logger.LogWarning(
                "EventSub conduit {ConduitId} was created under a different Twitch client ID and "
                + "cannot be managed with the current credentials; Twitch deletes it 72 hours after "
                + "its shard disconnected.",
                saved.ConduitId);
        }

        string? known = _conduitId ?? (saved?.ClientId == clientId ? saved.ConduitId : null);
        if (known is not null)
        {
            IReadOnlyList<string> existing = await api.GetConduitIdsAsync(ct);
            if (existing.Contains(known, StringComparer.Ordinal))
            {
                _conduitId = known;
                if (saved?.ConduitId != known || saved.ClientId != clientId)
                    await stateStore.SaveAsync(new ConduitState(known, clientId), ct);
                return known;
            }

            logger.LogWarning(
                "EventSub conduit {ConduitId} no longer exists on Twitch (it is deleted after 72 hours "
                + "without a connected shard); creating a new one.",
                known);
        }

        string created = await api.CreateConduitAsync(ct);
        _conduitId = created;
        logger.LogInformation("Created EventSub conduit {ConduitId}.", created);
        await stateStore.SaveAsync(new ConduitState(created, clientId), ct);
        return created;
    }

    private async Task<int> BindAndReconcileAsync(
        string conduitId, string sessionId, CancellationToken ct)
    {
        // Twitch closes the socket unless shard 0 is bound within 10 s of session_welcome,
        // so bind before the slower reconcile.
        await api.AssignShardAsync(conduitId, sessionId, ct);
        return await ReconcileAsync(conduitId, ct);
    }

    /// <summary>
    /// Brings the conduit's subscriptions in line with the watched channels and sets the
    /// all-or-nothing push status. A failed create (auth, 5xx, network) throws and the session
    /// restarts with backoff; a refusal leaves that channel polling-only.
    /// </summary>
    private async Task<int> ReconcileAsync(string conduitId, CancellationToken ct)
    {
        ChannelEntry[] watched = channels.Snapshot.ToArray();
        string[] userIds = watched.Select(channel => channel.UserId).OfType<string>().ToArray();

        ConduitSubscriptionList current = await api.ListSubscriptionsAsync(conduitId, ct);
        ConduitSubscriptionPlan plan = ConduitSubscriptionPlan.For(userIds, current.Subscriptions);

        foreach (string subscriptionId in plan.ToDelete)
        {
            try
            {
                await api.DeleteSubscriptionAsync(subscriptionId, ct);
            }
            catch (Exception ex) when (ex is TwitchApiException or HttpRequestException)
            {
                // A leftover costs budget but does not affect coverage; the next session retries.
                logger.LogWarning(
                    ex, "Could not delete EventSub subscription {SubscriptionId}.", subscriptionId);
            }
        }

        var active = new HashSet<ConduitSubscriptionKey>(plan.Active);
        bool verifyConflicts = false;
        foreach (ConduitSubscriptionKey key in plan.ToCreate)
        {
            switch (await api.CreateSubscriptionAsync(conduitId, key, ct))
            {
                case ConduitSubscribeOutcome.Created:
                    active.Add(key);
                    break;
                case ConduitSubscribeOutcome.AlreadyExists:
                    verifyConflicts = true;
                    break;
            }
        }

        if (verifyConflicts)
        {
            // A 409 counts as covered only if the existing subscription is enabled on our
            // conduit; one delivering elsewhere would only look active (spike memo §4.1).
            ConduitSubscriptionList after = await api.ListSubscriptionsAsync(conduitId, ct);
            active.UnionWith(ConduitSubscriptionPlan.For(userIds, after.Subscriptions).Active);
        }

        string[] covered = watched
            .Where(channel => channel.UserId is { } id && ConduitSubscriptionPlan.Covers(active, id))
            .Select(channel => channel.Login)
            .ToArray();
        schedule.SetPushStatus(PushStatus.FromCoverage(covered, watched.Length));
        logger.LogInformation(
            "EventSub conduit push coverage: {Covered}/{Total} channels "
            + "(created {Created}, deleted {Deleted}; cost before changes {TotalCost}/{MaxTotalCost}).",
            covered.Length,
            watched.Length,
            plan.ToCreate.Count,
            plan.ToDelete.Count,
            current.TotalCost,
            current.MaxTotalCost);
        return covered.Length;
    }

    private async Task RebindAsync(string conduitId, string sessionId, CancellationToken ct)
    {
        // Twitch does not document whether a shard follows session_reconnect, so bind the new
        // session explicitly while the old socket is still open. Repeating a bind is harmless.
        await api.AssignShardAsync(conduitId, sessionId, ct);
        logger.LogInformation("EventSub conduit shard rebound to the reconnected session.");
    }

    /// <summary>
    /// A conduit revocation affects one subscription, so the session keeps running. A watched
    /// channel loses full coverage until the next session reconciles it.
    /// </summary>
    private void Revoked(JsonElement payload)
    {
        string? type = null;
        string? status = null;
        string? broadcasterId = null;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("subscription", out JsonElement subscription)
            && subscription.ValueKind == JsonValueKind.Object)
        {
            type = ReadString(subscription, "type");
            status = ReadString(subscription, "status");
            if (subscription.TryGetProperty("condition", out JsonElement condition)
                && condition.ValueKind == JsonValueKind.Object)
            {
                broadcasterId = ReadString(condition, "broadcaster_user_id");
            }
        }

        logger.LogWarning(
            "EventSub {Type} for broadcaster {BroadcasterId} was revoked ({Status}); "
            + "that channel relies on polling until the next session.",
            type,
            broadcasterId,
            status);

        PushStatus current = schedule.Push;
        ChannelEntry? revoked = broadcasterId is null
            ? null
            : channels.Snapshot.FirstOrDefault(channel => channel.UserId == broadcasterId);
        if (revoked is not null && current.Covers(revoked.Login))
        {
            schedule.SetPushStatus(PushStatus.FromCoverage(
                current.CoveredLogins.Where(login => login != revoked.Login), current.Watched));
        }
        else if (broadcasterId is null && current.Covered > 0)
        {
            // Without a broadcaster ID we cannot tell which channel lost push, so trust none of
            // them until the next session reconciles; polling already covers every channel.
            schedule.SetPushStatus(PushStatus.FromCoverage([], current.Watched));
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed class Session(ConduitEventSubBinding owner, string conduitId)
        : IEventSubSessionBinding
    {
        public Task<int> OnWelcomeAsync(string sessionId, CancellationToken ct) =>
            owner.BindAndReconcileAsync(conduitId, sessionId, ct);

        public Task OnReconnectedAsync(string sessionId, CancellationToken ct) =>
            owner.RebindAsync(conduitId, sessionId, ct);

        public void OnRevocation(JsonElement payload) => owner.Revoked(payload);
    }
}
