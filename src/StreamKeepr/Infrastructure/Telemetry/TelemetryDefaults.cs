namespace StreamKeepr.Infrastructure.Telemetry;

/// <summary>Defaults for the OTLP exporter and its processors, overridable by standard OTEL_* settings.</summary>
internal static class TelemetryDefaults
{
    public const int ExportTimeoutMilliseconds = 3000;
    public const int BatchExportTimeoutMilliseconds = 3000;
    public const int BatchScheduleDelayMilliseconds = 2000;
    public const int MetricExportTimeoutMilliseconds = 3000;
    public const int MetricExportIntervalMilliseconds = 30_000;
}
