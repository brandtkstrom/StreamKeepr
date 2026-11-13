using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Hosting;

/// <summary>
/// Liveness for the DVR: the poller must keep ticking, channel loops must keep draining events,
/// and an active recording must keep producing media. Retry/cooldown/Unknown degrade but do not
/// kill the container.
/// </summary>
public sealed class DvrHealthCheck(
    DvrStatus status,
    PollSchedule schedule,
    IOptions<TwitchOptions> twitch,
    IOptions<RecorderOptions> recorder,
    TimeProvider clock) : IHealthCheck
{
    /// <summary>The registration name; the Settings page reads this check by name.</summary>
    public const string Name = "dvr";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        IReadOnlyDictionary<string, DvrSnapshot> channels = status.Current;
        TwitchOptions options = twitch.Value;

        TimeSpan pollBudget = TimeSpan.FromSeconds(
            3 * (schedule.IsPushConnected
                ? options.PollIntervalWithEventSubSeconds
                : options.PollIntervalSeconds));

        var data = new Dictionary<string, object>
        {
            ["channels"] = channels.ToDictionary(
                pair => pair.Key,
                pair => (object)pair.Value.State.GetType().Name),
            ["eventSub"] = schedule.IsPushConnected ? "connected" : "polling only",
            ["secondsSinceLastPoll"] = Math.Round((now - schedule.LastPollAt).TotalSeconds),
        };

        if (now - schedule.LastPollAt > pollBudget)
            return Result(HealthCheckResult.Unhealthy("The poller has stopped.", data: data));

        string[] stuck = channels
            .Where(pair => now - pair.Value.LastEventAt > pollBudget)
            .Select(pair => pair.Key)
            .ToArray();
        if (stuck.Length > 0)
        {
            return Result(HealthCheckResult.Unhealthy(
                $"Channel loop not processing events: {string.Join(", ", stuck)}.", data: data));
        }

        TimeSpan stallBudget = TimeSpan.FromSeconds(2 * recorder.Value.StallTimeoutSeconds);
        string[] stalled = channels
            .Where(pair => pair.Value.State is DvrState.Recording
                && (pair.Value.LastProgressAt ?? status.JobStartedAt(pair.Key)) is { } progress
                && now - progress > stallBudget)
            .Select(pair => pair.Key)
            .ToArray();
        if (stalled.Length > 0)
        {
            return Result(HealthCheckResult.Unhealthy(
                $"Recording without media progress: {string.Join(", ", stalled)}.", data: data));
        }

        string[] troubled = channels
            .Where(pair => pair.Value.State is DvrState.CoolingDown or DvrState.WaitingToRetry
                || pair.Value.LastObservation is StreamObservation.Unknown)
            .Select(pair => pair.Key)
            .ToArray();
        if (troubled.Length > 0)
        {
            return Result(HealthCheckResult.Degraded(
                $"Retrying or Twitch unreachable: {string.Join(", ", troubled)}.", data: data));
        }

        return Result(HealthCheckResult.Healthy(
            $"{channels.Count} channels watched.", data: data));
    }

    private static Task<HealthCheckResult> Result(HealthCheckResult result) =>
        Task.FromResult(result);
}
