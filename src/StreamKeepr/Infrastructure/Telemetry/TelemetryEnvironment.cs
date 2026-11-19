using Microsoft.Extensions.Hosting;

namespace StreamKeepr.Infrastructure.Telemetry;

/// <summary>Maps host environment names to the deployment environment resource attribute.</summary>
internal static class TelemetryEnvironment
{
    public static string Name(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsEnvironment("Mock") || environment.IsEnvironment("Testing"))
            return "test";

        return environment.EnvironmentName.ToLowerInvariant();
    }
}
