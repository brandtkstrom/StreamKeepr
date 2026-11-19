namespace StreamKeepr.Infrastructure.Telemetry;

/// <summary>
/// The OpenTelemetry switches that turn off URL query redaction. The app never needs unredacted
/// query values (OAuth codes and state travel in queries), so startup refuses either one.
/// </summary>
internal static class TelemetryQueryRedaction
{
    public static readonly IReadOnlyList<string> DisableVariables =
    [
        "OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION",
        "OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION",
    ];

    /// <summary>
    /// True when either variable is set to any non-blank value. Presence alone counts, so an
    /// unexpected spelling (<c>yes</c>, <c>1</c>) can never slip past as "not true".
    /// </summary>
    public static bool DisableRequested(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return DisableVariables.Any(key => !string.IsNullOrWhiteSpace(configuration[key]));
    }
}
