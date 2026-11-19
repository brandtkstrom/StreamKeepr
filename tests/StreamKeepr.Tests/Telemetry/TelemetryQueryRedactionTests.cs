using Microsoft.Extensions.Configuration;
using StreamKeepr.Infrastructure.Telemetry;

namespace StreamKeepr.Tests.Telemetry;

public sealed class TelemetryQueryRedactionTests
{
    [Theory]
    [InlineData("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "true")]
    [InlineData("OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION", "true")]
    [InlineData("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "false")]
    [InlineData("OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION", "yes")]
    public void Any_value_for_either_variable_counts_as_disabling(string key, string value)
    {
        IConfiguration configuration = Configuration(new() { [key] = value });

        Assert.True(TelemetryQueryRedaction.DisableRequested(configuration));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_or_blank_variable_keeps_redaction(string? value)
    {
        IConfiguration configuration = Configuration(new()
        {
            ["OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION"] = value,
            ["OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION"] = value,
        });

        Assert.False(TelemetryQueryRedaction.DisableRequested(configuration));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
