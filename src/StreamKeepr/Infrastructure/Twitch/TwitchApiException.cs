namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>Twitch/Helix could not answer; distinct from a known-unknown login.</summary>
public sealed class TwitchApiException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
}
