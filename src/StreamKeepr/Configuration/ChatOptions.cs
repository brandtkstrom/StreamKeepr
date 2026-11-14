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

    [Range(30, 3600)] public int IdleTimeoutSeconds { get; set; } = 300;

    [Range(10, 3600)] public int ReconnectMaxSeconds { get; set; } = 300;

    [Range(1, long.MaxValue)] public long MaxBytesPerSession { get; set; } = 512L * 1024 * 1024;

    public ChatPurgeMode PurgeDeletedText { get; set; } = ChatPurgeMode.Off;

    public bool HasToken => !string.IsNullOrWhiteSpace(this.TwitchUserOAuthToken);
}
