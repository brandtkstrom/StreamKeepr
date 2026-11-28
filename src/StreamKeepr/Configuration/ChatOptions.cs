using System.ComponentModel.DataAnnotations;

namespace StreamKeepr.Configuration;

public enum ChatPurgeMode
{
    Off,
    OnFinalize,
}

/// <summary>
/// Chat capture is a best-effort read-only tap of Twitch IRC. It never gates recording.
/// The feature is off by default and is intended for the project's private-home-use scope.
/// </summary>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    public bool Enabled { get; set; }

    /// <summary>Optional dedicated user token with <c>chat:read</c>; never the admin token.</summary>
    public string? TwitchUserOAuthToken { get; set; }

    /// <summary>The login for <see cref="TwitchUserOAuthToken"/>; required when a token is set.</summary>
    public string? TwitchLogin { get; set; }

    [Range(0, 60)] public int LeadInSeconds { get; set; } = 12;

    [Range(1, 60)] public int BucketMinutes { get; set; } = 10;

    [Range(1, 100)] public int MaxChannels { get; set; } = 100;

    [Range(100, 5000)] public int JoinIntervalMs { get; set; } = 600;

    [Range(1, 60)] public int ConnectTimeoutSeconds { get; set; } = 10;

    [Range(1, 60)] public int RegistrationTimeoutSeconds { get; set; } = 15;

    [Range(10, 3600)] public int ReconnectMaxSeconds { get; set; } = 300;

    [Range(1, long.MaxValue)] public long MaxBytesPerSession { get; set; } = 512L * 1024 * 1024;

    public ChatPurgeMode PurgeDeletedText { get; set; } = ChatPurgeMode.Off;

    public ChatEmoteOptions Emotes { get; set; } = new();

    public int BucketSeconds => BucketMinutes * 60;

    public bool HasToken => !string.IsNullOrWhiteSpace(TwitchUserOAuthToken);
}

/// <summary>Third-party emote rendering in chat replay; off by default.</summary>
public sealed class ChatEmoteOptions
{
    public EmoteProviderOptions Bttv { get; set; } = new();

    public EmoteProviderOptions SevenTv { get; set; } = new();

    public EmoteProviderOptions Ffz { get; set; } = new();

    /// <summary>
    /// Loads the animated asset instead of the static first frame. Off by default: BetterTTV's
    /// animated assets are far larger than its static ones.
    /// </summary>
    public bool Animated { get; set; }
}

/// <summary>One third-party emote provider's switch; each provider reads its own instance.</summary>
public sealed class EmoteProviderOptions
{
    public bool Enabled { get; set; }
}
