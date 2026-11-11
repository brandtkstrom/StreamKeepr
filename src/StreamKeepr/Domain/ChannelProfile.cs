namespace StreamKeepr.Domain;

/// <summary>The Twitch profile fields persisted with a watched channel.</summary>
public sealed record ChannelProfile(
    string UserId, string DisplayName, string? AvatarUrl);
