using System.Diagnostics.Metrics;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Infrastructure.Telemetry;

/// <summary>
/// Observable gauges over cached runtime snapshots. Registered only when telemetry is enabled;
/// every callback must return quickly and must not perform I/O.
/// </summary>
public sealed class StreamKeeprRuntimeMetrics
{
    private static readonly (string Name, Func<DvrState, bool> Match)[] States =
    [
        ("idle", state => state is DvrState.Idle),
        ("recording", state => state is DvrState.Recording),
        ("waiting_to_retry", state => state is DvrState.WaitingToRetry),
        ("cooling_down", state => state is DvrState.CoolingDown),
        ("paused", state => state is DvrState.Paused),
    ];

    private readonly DvrStatus _status;
    private readonly PollSchedule _schedule;
    private readonly IStorageMonitor _storage;
    private readonly IEventSubClient _eventSub;
    private readonly TimeProvider _clock;

    public StreamKeeprRuntimeMetrics(
        IMeterFactory meterFactory,
        DvrStatus status,
        PollSchedule schedule,
        IStorageMonitor storage,
        IEventSubClient eventSub,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(eventSub);
        ArgumentNullException.ThrowIfNull(clock);

        _status = status;
        _schedule = schedule;
        _storage = storage;
        _eventSub = eventSub;
        _clock = clock;

        Meter meter = meterFactory.Create(
            new MeterOptions(StreamKeeprTelemetry.MeterName)
            {
                Version = StreamKeeprTelemetry.ServiceVersion,
            });

        meter.CreateObservableGauge(
            "streamkeepr.dvr.channels",
            ObserveChannels,
            unit: "{channel}",
            description: "Watched channels by DVR state.");
        meter.CreateObservableGauge(
            "streamkeepr.twitch.poll.age",
            ObservePollAge,
            unit: "s",
            description: "Seconds since the last poll attempt.");
        meter.CreateObservableGauge(
            "streamkeepr.twitch.push.coverage",
            ObservePushCoverage,
            unit: "1",
            description: "Fraction of watched channels covered by EventSub.");
        meter.CreateObservableGauge(
            "streamkeepr.eventsub.connected",
            ObserveEventSubConnected,
            unit: "1",
            description: "Whether an EventSub session is up and covering channels.");
        meter.CreateObservableGauge(
            "streamkeepr.storage.free",
            () => _storage.Current.FreeBytes,
            unit: "By",
            description: "Free bytes on the recordings volume.");
    }

    private IEnumerable<Measurement<int>> ObserveChannels()
    {
        var counts = States.ToDictionary(state => state.Name, _ => 0, StringComparer.Ordinal);

        foreach (DvrSnapshot snapshot in _status.Current.Values)
        {
            foreach ((string name, Func<DvrState, bool> match) in States)
            {
                if (match(snapshot.State))
                {
                    counts[name]++;
                    break;
                }
            }
        }

        foreach ((string name, int count) in counts)
        {
            yield return new Measurement<int>(
                count,
                new KeyValuePair<string, object?>("streamkeepr.dvr.state", name));
        }
    }

    private double ObservePollAge() =>
        Math.Max(0, (_clock.GetUtcNow() - _schedule.LastPollAt).TotalSeconds);

    private double ObservePushCoverage()
    {
        PushStatus push = _schedule.Push;
        return push.Watched == 0 ? 1 : (double)push.Covered / push.Watched;
    }

    private Measurement<int> ObserveEventSubConnected() =>
        new(
            _schedule.Push.HasSession ? 1 : 0,
            new KeyValuePair<string, object?>(
                "streamkeepr.eventsub.transport", _eventSub.Transport));
}
