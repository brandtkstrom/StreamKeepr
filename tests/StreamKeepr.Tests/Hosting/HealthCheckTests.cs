using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Storage;

namespace StreamKeepr.Tests.Hosting;

public sealed class HealthCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(
        new DateTimeOffset(2026, 11, 10, 16, 30, 0, TimeSpan.Zero));

    public HealthCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Healthy_with_no_channels_and_a_recent_poll()
    {
        DvrHealthCheck check = CreateDvrCheck(out _);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Stale_poll_uses_the_short_budget_without_push()
    {
        DvrHealthCheck check = CreateDvrCheck(out FakeTimeProvider clock);

        clock.Advance(TimeSpan.FromSeconds(91));
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("poller", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Full_push_coverage_uses_the_long_poll_budget()
    {
        DvrHealthCheck check = CreateDvrCheck(out FakeTimeProvider clock, pushConnected: true);

        clock.Advance(TimeSpan.FromSeconds(899));
        HealthCheckResult healthy = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, healthy.Status);

        clock.Advance(TimeSpan.FromSeconds(2));
        HealthCheckResult stale = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, stale.Status);
    }

    [Fact]
    public async Task A_stuck_channel_loop_is_unhealthy()
    {
        DvrHealthCheck check = CreateDvrCheck(
            out FakeTimeProvider clock, out DvrStatus status, out PollSchedule schedule);
        status.Applied("streamer", null, new DvrState.Idle(), clock.GetUtcNow());

        clock.Advance(TimeSpan.FromSeconds(91));
        schedule.Polled();
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Channel loop", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recording_without_media_progress_is_unhealthy()
    {
        DvrHealthCheck check = CreateDvrCheck(
            out FakeTimeProvider clock, out DvrStatus status, out PollSchedule schedule);
        var job = new JobId("session", 1);
        var session = new Session(
            "20261110T163000Z_1", "streamer", "1", clock.GetUtcNow(), "/tmp/session");
        var recording = new DvrState.Recording(
            session,
            job,
            new LiveStream("1", "Title", "Game", clock.GetUtcNow(), null),
            Attempt: 0,
            HasMedia: true,
            Streak: 0,
            FirstMediaAt: clock.GetUtcNow());

        status.Applied("streamer", null, recording, clock.GetUtcNow());
        status.ReportProgress("streamer", job, 100, TimeSpan.FromSeconds(1), clock.GetUtcNow());

        clock.Advance(TimeSpan.FromSeconds(241));
        schedule.Polled();
        status.Applied("streamer", null, recording, clock.GetUtcNow());
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("media progress", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_recording_that_never_produced_media_is_unhealthy()
    {
        DvrHealthCheck check = CreateDvrCheck(
            out FakeTimeProvider clock, out DvrStatus status, out PollSchedule schedule);
        var job = new JobId("session", 1);
        var session = new Session(
            "20261110T163000Z_1", "streamer", "1", clock.GetUtcNow(), "/tmp/session");
        var recording = new DvrState.Recording(
            session,
            job,
            new LiveStream("1", "Title", "Game", clock.GetUtcNow(), null),
            Attempt: 0,
            HasMedia: false,
            Streak: 0);

        status.Applied("streamer", null, recording, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(241));
        schedule.Polled();
        status.Applied("streamer", null, recording, clock.GetUtcNow());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("media progress", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retrying_or_unknown_is_degraded()
    {
        DvrHealthCheck check = CreateDvrCheck(out FakeTimeProvider clock, out DvrStatus status);
        status.Applied(
            "streamer",
            new DvrEvent.StreamStatusObserved(new StreamObservation.Unknown("outage"), clock.GetUtcNow()),
            new DvrState.WaitingToRetry(
                new Session("20261110T163000Z_1", "streamer", "1", clock.GetUtcNow(), "/tmp/session"),
                new JobId("session", 2),
                new LiveStream("1", "Title", "Game", clock.GetUtcNow(), null),
                Attempt: 1,
                Streak: 1),
            clock.GetUtcNow());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Cooling_down_is_degraded()
    {
        DvrHealthCheck check = CreateDvrCheck(out FakeTimeProvider clock, out DvrStatus status);
        status.Applied(
            "streamer",
            null,
            new DvrState.CoolingDown(
                clock.GetUtcNow().AddMinutes(1), Resume: null, "failed"),
            clock.GetUtcNow());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Unknown_observation_alone_is_degraded()
    {
        DvrHealthCheck check = CreateDvrCheck(out FakeTimeProvider clock, out DvrStatus status);
        status.Applied(
            "streamer",
            new DvrEvent.StreamStatusObserved(
                new StreamObservation.Unknown("outage"), clock.GetUtcNow()),
            new DvrState.Idle(),
            clock.GetUtcNow());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Disk_check_uses_minimum_and_double_minimum()
    {
        var storage = Options.Create(new StorageOptions
        {
            RecordingsRoot = _root,
            MinFreeGb = 5,
        });
        const long gib = DiskGuard.BytesPerGib;

        var unhealthy = new DiskSpaceHealthCheck(
            new DiskGuard(storage, new FixedDiskSpace(4 * gib, 10 * gib)), storage);
        var degraded = new DiskSpaceHealthCheck(
            new DiskGuard(storage, new FixedDiskSpace(6 * gib, 10 * gib)), storage);
        var healthy = new DiskSpaceHealthCheck(
            new DiskGuard(storage, new FixedDiskSpace(10 * gib, 20 * gib)), storage);

        Assert.Equal(HealthStatus.Unhealthy, (await unhealthy.CheckHealthAsync(
            new HealthCheckContext())).Status);
        Assert.Equal(HealthStatus.Degraded, (await degraded.CheckHealthAsync(
            new HealthCheckContext())).Status);
        Assert.Equal(HealthStatus.Healthy, (await healthy.CheckHealthAsync(
            new HealthCheckContext())).Status);

        var boundary = new DiskSpaceHealthCheck(
            new DiskGuard(storage, new FixedDiskSpace(5 * gib, 10 * gib)), storage);
        Assert.Equal(HealthStatus.Degraded, (await boundary.CheckHealthAsync(
            new HealthCheckContext())).Status);
        var exactlyDouble = new DiskSpaceHealthCheck(
            new DiskGuard(storage, new FixedDiskSpace(10 * gib, 20 * gib)), storage);
        Assert.Equal(HealthStatus.Healthy, (await exactlyDouble.CheckHealthAsync(
            new HealthCheckContext())).Status);
    }

    [Fact]
    public async Task Library_check_waits_for_recovery()
    {
        var library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);
        var check = new LibraryLoadedHealthCheck(library);

        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(
            new HealthCheckContext())).Status);

        library.MarkLoaded();
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(
            new HealthCheckContext())).Status);
    }

    [Fact]
    public async Task Host_exposes_live_and_ready_endpoints()
    {
        using var factory = new StreamKeeprAppFactory(
            new Dictionary<string, string?>
            {
                [$"{StorageOptions.SectionName}:MinFreeGb"] = "1",
            },
            configureTestServices: services => services.AddSingleton<IDiskSpace>(
                new FixedDiskSpace(10 * DiskGuard.BytesPerGib, 20 * DiskGuard.BytesPerGib)));
        using var client = factory.CreateClient();

        using HttpResponseMessage live = await client.GetAsync("/health/live");
        using HttpResponseMessage ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    private DvrHealthCheck CreateDvrCheck(out FakeTimeProvider clock) =>
        CreateDvrCheckImplementation(out clock, out _, out _, pushConnected: false);

    private DvrHealthCheck CreateDvrCheck(
        out FakeTimeProvider clock, bool pushConnected = false) =>
        CreateDvrCheckImplementation(out clock, out _, out _, pushConnected);

    private DvrHealthCheck CreateDvrCheck(
        out FakeTimeProvider clock, out DvrStatus status) =>
        CreateDvrCheckImplementation(out clock, out status, out _, pushConnected: false);

    private DvrHealthCheck CreateDvrCheck(
        out FakeTimeProvider clock, out DvrStatus status, out PollSchedule schedule) =>
        CreateDvrCheckImplementation(out clock, out status, out schedule, pushConnected: false);

    private sealed class FixedDiskSpace(long freeBytes, long totalBytes) : IDiskSpace
    {
        public long AvailableFreeSpace(string path) => freeBytes;

        public long TotalSize(string path) => totalBytes;
    }

    private DvrHealthCheck CreateDvrCheckImplementation(
        out FakeTimeProvider clock,
        out DvrStatus status,
        out PollSchedule schedule,
        bool pushConnected)
    {
        clock = _clock;
        status = new DvrStatus();
        schedule = new PollSchedule(clock);
        schedule.SetPushStatus(pushConnected ? PushStatus.AllConnected : PushStatus.PollingOnly);

        return new DvrHealthCheck(
            status,
            schedule,
            Options.Create(new TwitchOptions()),
            Options.Create(new RecorderOptions { StallTimeoutSeconds = 120 }),
            clock);
    }
}
