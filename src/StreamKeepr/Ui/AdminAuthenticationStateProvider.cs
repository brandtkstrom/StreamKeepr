using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace StreamKeepr.Ui;

/// <summary>
/// Per-circuit admin session flags the top bar and Settings read. A new page load starts a new
/// circuit, so signing in or out through the HTTP endpoints clears it.
/// </summary>
public sealed class AdminSessionState
{
    public bool Expired { get; private set; }

    /// <summary>The login that was signed in when the session expired.</summary>
    public string? LastLogin { get; private set; }

    public event Action? Changed;

    public void MarkExpired(string? login)
    {
        Expired = true;
        LastLogin = login;
        Changed?.Invoke();
    }
}

/// <summary>Checks that the signed-in admin's stored Twitch token is still accepted.</summary>
public interface IAdminSessionValidator
{
    /// <returns>False only when Twitch definitely rejects the session; transient failures pass.</returns>
    Task<bool> IsValidAsync(ClaimsPrincipal user, CancellationToken ct);
}

/// <summary>
/// The circuit's authentication state. Every <see cref="RevalidationInterval"/> it re-checks a
/// signed-in admin through <see cref="IAdminSessionValidator"/>; on failure the circuit signs
/// out, <see cref="AuthenticationStateProvider.AuthenticationStateChanged"/> fires so admin
/// controls go inert, and <see cref="AdminSessionState"/> records the expiry. Playback and the
/// page are untouched. The interval runs on <see cref="TimeProvider"/> so tests can fake it.
/// </summary>
public sealed class AdminAuthenticationStateProvider :
    AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider, IDisposable
{
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(30);

    private static readonly Task<AuthenticationState> Anonymous =
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

    private readonly IAdminSessionValidator _validator;
    private readonly AdminSessionState _session;
    private readonly TimeProvider _clock;
    private readonly ILogger<AdminAuthenticationStateProvider> _logger;
    private readonly CancellationTokenSource _stop = new();

    private Task<AuthenticationState> _state = Anonymous;
    private bool _looping;

    public AdminAuthenticationStateProvider(
        IAdminSessionValidator validator,
        AdminSessionState session,
        TimeProvider clock,
        ILogger<AdminAuthenticationStateProvider> logger)
    {
        _validator = validator;
        _session = session;
        _clock = clock;
        _logger = logger;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state;

    /// <summary>Called by the framework with the request's user when the circuit starts.</summary>
    public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
    {
        ArgumentNullException.ThrowIfNull(authenticationStateTask);
        _state = authenticationStateTask;
        NotifyAuthenticationStateChanged(_state);

        if (!_looping)
        {
            _looping = true;
            _ = RevalidateLoopAsync(_stop.Token);
        }
    }

    private async Task RevalidateLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(RevalidationInterval, _clock, ct);

                ClaimsPrincipal user = (await _state).User;
                if (user.Identity?.IsAuthenticated != true)
                    continue;

                bool valid;
                try
                {
                    valid = await _validator.IsValidAsync(user, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An unexpected validator failure must not sign the admin out.
                    _logger.LogWarning(ex, "Could not revalidate the Twitch admin session.");
                    continue;
                }

                if (valid)
                    continue;

                _logger.LogInformation("The Twitch admin session expired; signing this circuit out.");
                _state = Anonymous;
                _session.MarkExpired(user.Identity?.Name);
                NotifyAuthenticationStateChanged(_state);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The circuit closed.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
