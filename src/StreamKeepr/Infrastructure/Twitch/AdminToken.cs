namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// The admin's Twitch user token. <see cref="ToString"/> redacts both token
/// values so the record can appear in logs, debuggers, and test failures.
/// </summary>
public sealed record AdminToken(
    string Login, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        $"AdminToken {{ Login = {Login}, AccessToken = [redacted], " +
        $"RefreshToken = [redacted], ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>
/// Owns the admin's Twitch user token, encrypted at rest. EventSub is the only
/// consumer; the browser never receives these values.
/// </summary>
public interface IAdminTokenStore
{
    /// <summary>Raised after a token is saved or cleared.</summary>
    event Action? Changed;

    /// <summary>
    /// Returns the current access token, refreshing it near expiry. Returns
    /// <c>null</c> when no readable token is stored.
    /// </summary>
    ValueTask<string?> GetAsync(CancellationToken ct);

    /// <summary>Replaces the stored token. The latest admin sign-in wins.</summary>
    Task SaveAsync(AdminToken token, CancellationToken ct);

    /// <summary>Deletes the stored token. Existing waiters stay waiting.</summary>
    Task ClearAsync(CancellationToken ct);

    /// <summary>Completes when a readable token is available.</summary>
    Task WaitForTokenAsync(CancellationToken ct);
}
