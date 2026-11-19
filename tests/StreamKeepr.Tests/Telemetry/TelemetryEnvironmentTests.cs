using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreamKeepr.Infrastructure.Telemetry;

namespace StreamKeepr.Tests.Telemetry;

public sealed class TelemetryEnvironmentTests
{
    [Theory]
    [InlineData("Development", "development")]
    [InlineData("Production", "production")]
    [InlineData("Staging", "production")]
    [InlineData("Mock", "test")]
    [InlineData("Testing", "test")]
    public void Maps_the_host_environment(string hostEnvironment, string expected)
    {
        using IHost host = new HostBuilder().UseEnvironment(hostEnvironment).Build();

        Assert.Equal(
            expected,
            TelemetryEnvironment.Name(host.Services.GetRequiredService<IHostEnvironment>()));
    }
}
