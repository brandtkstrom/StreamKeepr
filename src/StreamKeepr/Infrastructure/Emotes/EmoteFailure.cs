using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Infrastructure.Emotes;

/// <summary>
/// Classifies an emote catalog or channel-resolution failure into the structured
/// <c>HttpStatus</c> and <c>Reason</c> log properties. <c>HttpStatus</c> is null unless a
/// real HTTP response status is known, never a placeholder.
/// </summary>
internal static class EmoteFailure
{
    public static (int? HttpStatus, string Reason) Describe(Exception exception) =>
        exception switch
        {
            EmoteProviderException { Kind: EmoteProviderFailureKind.InvalidPayload } =>
                (null, "invalid-payload"),
            EmoteProviderException { Kind: EmoteProviderFailureKind.TooLarge } =>
                (null, "too-large"),
            EmoteProviderException providerError => (providerError.HttpStatus, "http"),
            OperationCanceledException => (null, "timeout"),
            HttpRequestException requestError => ((int?)requestError.StatusCode, "transport"),
            // TwitchApiException carries its HTTP status only in message text, never as data.
            TwitchApiException => (null, "twitch-api"),
            _ => (null, "error"),
        };
}
