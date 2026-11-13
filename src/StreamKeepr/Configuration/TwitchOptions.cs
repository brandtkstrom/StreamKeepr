using System.ComponentModel.DataAnnotations;

namespace StreamKeepr.Configuration;

public sealed class TwitchOptions
{
    public const string SectionName = "Twitch";

    [Required] public string ClientId { get; set => field = value?.Trim() ?? ""; } = "";

    [Required] public string ClientSecret { get; set => field = value?.Trim() ?? ""; } = "";

    // Seed list only: copied into channels.json on first start; after that the Settings page owns the list.
    public List<string> Channels
    {
        get;
        set => field = value is null
            ? []
            : value
                .Select(TwitchLogin.Normalize)
                .Distinct(StringComparer.Ordinal)
                .ToList();
    } = [];

    [Range(10, 600)] public int PollIntervalSeconds { get; set; } = 30;

    [Range(60, 3600)] public int PollIntervalWithEventSubSeconds { get; set; } = 300;

    // How EventSub push is delivered; see docs/adr/0006-eventsub-conduit-transport.md.
    [EnumDataType(typeof(EventSubTransportMode))]
    public EventSubTransportMode EventSubTransport { get; set; } = EventSubTransportMode.WebSocket;
}

/// <summary>The EventSub transport. Polling stays the safety net in both modes.</summary>
public enum EventSubTransportMode
{
    /// <summary>User-token WebSocket; needs a signed-in admin and covers about five channels.</summary>
    WebSocket,

    /// <summary>App-token conduit with one WebSocket shard; independent of admin sign-in.</summary>
    Conduit,
}
