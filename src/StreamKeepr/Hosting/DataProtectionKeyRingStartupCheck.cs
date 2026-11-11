using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Logging;

namespace StreamKeepr.Hosting;

/// <summary>
/// Loads the key ring during startup so an empty or unreadable ring is logged
/// before authentication or token storage starts serving requests.
/// </summary>
internal sealed class DataProtectionKeyRingStartupCheck(
    IServiceProvider serviceProvider,
    ILogger<DataProtectionKeyRingStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the key manager here, not in the constructor: hosted services
            // are all constructed before any StartAsync runs, and resolving it early
            // would probe the key directory before ToolPreflight.
            _ = serviceProvider.GetRequiredService<IKeyManager>().GetAllKeys();
        }
        catch (Exception ex)
        {
            // The repository wrapper already degrades damaged files to an empty ring.
            // This guard covers key-manager-level corruption (for example duplicate key
            // ids) so startup still succeeds and existing payloads can be rejected.
            logger.LogError(
                ex,
                "The Data Protection key ring could not be loaded. Existing cookies, antiforgery " +
                "tokens, and encrypted admin tokens cannot be read; affected users must re-authenticate.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
