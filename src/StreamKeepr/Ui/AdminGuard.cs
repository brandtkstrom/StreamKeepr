using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace StreamKeepr.Ui;

/// <summary>The server-side guard for admin-only view models; the UI hides, this enforces.</summary>
internal static class AdminGuard
{
    /// <exception cref="UnauthorizedAccessException">The circuit has no signed-in admin.</exception>
    public static void Require(Task<AuthenticationState> state)
    {
        ClaimsPrincipal? user = state.IsCompletedSuccessfully ? state.Result.User : null;
        if (user?.Identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException("An admin must be signed in with Twitch.");
    }
}
