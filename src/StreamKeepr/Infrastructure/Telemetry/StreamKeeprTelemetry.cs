using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Telemetry;

/// <summary>
/// Owns the application activity source and meter. The singleton is registered even when
/// telemetry is disabled; instruments are no-ops without a provider and recording methods
/// must never throw into domain code.
/// </summary>
public sealed class StreamKeeprTelemetry : IDisposable
{
    public const string SourceName = "StreamKeepr";
    public const string MeterName = "StreamKeepr";
    public const string ServiceName = "streamkeepr";
    public const string RecorderExitKindAttribute = "recorder.exit_kind";
    public const string ExportOutcomeAttribute = "export.outcome";
    public const string EventSubTransportAttribute = "eventsub.transport";
    public const string DvrStateAttribute = "dvr.state";

    public static readonly string ServiceVersion =
        typeof(StreamKeeprTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(StreamKeeprTelemetry).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static readonly double[] DurationBuckets =
        [5, 15, 30, 60, 300, 900, 1800, 3600, 7200, 14400];

    private static readonly long[] SizeBuckets =
        [1L << 20, 10L << 20, 100L << 20, 500L << 20, 1L << 30, 5L << 30, 10L << 30, 50L << 30];

    private readonly Counter<long> _parts;
    private readonly Histogram<double> _partDuration;
    private readonly Histogram<long> _partSize;
    private readonly Counter<long> _stalls;
    private readonly Counter<long> _forceKills;
    private readonly Counter<long> _chatDropped;
    private readonly Counter<long> _exportJobs;
    private readonly Counter<long> _eventSubReconnects;

    public StreamKeeprTelemetry(IMeterFactory meterFactory, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Enabled = enabled;
        Meter = meterFactory.Create(new MeterOptions(MeterName) { Version = ServiceVersion });
        ActivitySource = new ActivitySource(SourceName, ServiceVersion);

        _parts = Meter.CreateCounter<long>(
            "streamkeepr.recorder.parts",
            unit: "{part}",
            description: "Recorder parts by exit kind.");
        _partDuration = Meter.CreateHistogram<double>(
            "streamkeepr.recorder.part.duration",
            unit: "s",
            description: "Recorder part duration by exit kind.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
        _partSize = Meter.CreateHistogram<long>(
            "streamkeepr.recorder.part.size",
            unit: "By",
            description: "Recorder part size by exit kind.",
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = SizeBuckets });
        _stalls = Meter.CreateCounter<long>(
            "streamkeepr.recorder.stalls",
            unit: "{stall}",
            description: "Watchdog-triggered recorder stalls.");
        _forceKills = Meter.CreateCounter<long>(
            "streamkeepr.recorder.force_kills",
            unit: "{kill}",
            description: "Recorder processes that required a forced stop.");
        _chatDropped = Meter.CreateCounter<long>(
            "streamkeepr.chat.dropped",
            unit: "{message}",
            description: "Chat events dropped by a full capture queue.");
        _exportJobs = Meter.CreateCounter<long>(
            "streamkeepr.export.jobs",
            unit: "{job}",
            description: "Export jobs by outcome.");
        _eventSubReconnects = Meter.CreateCounter<long>(
            "streamkeepr.eventsub.reconnects",
            unit: "{reconnect}",
            description: "EventSub reconnect attempts by transport.");
    }

    public ActivitySource ActivitySource { get; }

    public Meter Meter { get; }

    public bool Enabled { get; }

    public void PartExited(RecorderExitKind kind, TimeSpan duration, long bytes)
    {
        if (!Enabled)
            return;

        var tag = new KeyValuePair<string, object?>(
            RecorderExitKindAttribute, ExitKind(kind));

        _parts.Add(1, tag);
        _partDuration.Record(duration.TotalSeconds, tag);
        _partSize.Record(bytes, tag);
    }

    public void Stall()
    {
        if (Enabled)
            _stalls.Add(1);
    }

    public void ForceKill()
    {
        if (Enabled)
            _forceKills.Add(1);
    }

    public void ChatDropped(int count)
    {
        if (Enabled && count > 0)
            _chatDropped.Add(count);
    }

    public void ExportFinished(string outcome)
    {
        if (Enabled)
            _exportJobs.Add(
                1,
                new KeyValuePair<string, object?>(ExportOutcomeAttribute, outcome));
    }

    public void EventSubReconnectAttempt(string transport)
    {
        if (Enabled)
            _eventSubReconnects.Add(
                1,
                new KeyValuePair<string, object?>(EventSubTransportAttribute, transport));
    }

    public void Dispose() => ActivitySource.Dispose();

    private static string ExitKind(RecorderExitKind kind) => kind switch
    {
        RecorderExitKind.Exited => "exited",
        RecorderExitKind.Stopped => "stopped",
        RecorderExitKind.Killed => "killed",
        _ => "unknown",
    };
}
