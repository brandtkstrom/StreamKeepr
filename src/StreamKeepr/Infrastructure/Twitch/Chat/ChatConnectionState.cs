namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>The IRC capture connection state, logged on each transition.</summary>
public enum ChatConnectionState
{
    Disabled,
    Idle,
    Connecting,
    Registering,
    Joining,
    Live,
    Backoff,
    Unavailable,
}
