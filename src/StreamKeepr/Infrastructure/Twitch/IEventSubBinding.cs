using System.Text.Json;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// The transport-specific half of EventSub: how a session authenticates, how its WebSocket is
/// bound to subscriptions, and what a revocation means. <see cref="EventSubClient"/> owns the
/// socket loop, keepalive, reconnect handoff, backoff, and notification routing for both modes.
/// </summary>
public interface IEventSubBinding
{
    /// <summary>Raised when the running session must restart for a binding-specific reason.</summary>
    event Action? RestartRequested;

    /// <summary>
    /// Prepares one EventSub session. Returns null when push cannot start yet; the binding has
    /// then already set the push status and waited, so the client loops again without backoff.
    /// Throws when setup failed and the client should back off before retrying.
    /// </summary>
    Task<IEventSubSessionBinding?> BeginSessionAsync(CancellationToken ct);
}

/// <summary>Callbacks for one EventSub session; see <see cref="IEventSubBinding"/>.</summary>
public interface IEventSubSessionBinding
{
    /// <summary>
    /// A fresh connection sent <c>session_welcome</c>. Makes the watched channels' subscriptions
    /// deliver to <paramref name="sessionId"/>, sets the push status, and returns how many
    /// watched channels are fully covered.
    /// </summary>
    Task<int> OnWelcomeAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// A reconnect handoff's new socket sent <c>session_welcome</c>. Called before the old
    /// socket closes.
    /// </summary>
    Task OnReconnectedAsync(string sessionId, CancellationToken ct);

    /// <summary>Twitch revoked a subscription; <paramref name="payload"/> is the message payload.</summary>
    void OnRevocation(JsonElement payload);
}

/// <summary>Ends the current EventSub session; the client reconnects with backoff.</summary>
internal sealed class EventSubRestartException(string message) : Exception(message);
