using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Hosting;

public static class HostLifecycle
{
    /// <summary>Registers the startup summary and shutdown breadcrumb.</summary>
    public static IServiceCollection AddStreamKeeprHostLifecycleLog(this IServiceCollection services) =>
        services.AddHostedService<HostLifecycleLog>();
}

/// <summary>
/// Host lifecycle breadcrumbs: one startup summary once the host has started and one line when
/// shutdown begins (SIGTERM, <c>docker compose stop</c>). The summary names safe values only;
/// it never logs an options object, so credentials and tokens cannot leak through it.
/// </summary>
internal sealed class HostLifecycleLog(
    IHostApplicationLifetime lifetime,
    IHostEnvironment environment,
    ChannelStore channels,
    IEventSubClient eventSub,
    IOptions<StorageOptions> storage,
    IOptions<ChatOptions> chat,
    ILogger<HostLifecycleLog> logger) : IHostedService
{
    private CancellationTokenRegistration _started;
    private CancellationTokenRegistration _stopping;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _started = lifetime.ApplicationStarted.Register(LogStarted);
        _stopping = lifetime.ApplicationStopping.Register(LogStopping);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _started.DisposeAsync();
        await _stopping.DisposeAsync();
    }

    // The supervisor loads channels.json during startup, so the store is current by now.
    // The EventSub client names the transport actually in use, "Mock" under the Mock profile.
    private void LogStarted() =>
        logger.LogInformation(
            "Stream Keepr started in {Environment}: {ChannelCount} watched channels, "
            + "recordings root {Root}, EventSub transport {Transport}, chat capture enabled: {ChatCaptureEnabled}.",
            environment.EnvironmentName,
            channels.Snapshot.Count,
            storage.Value.RecordingsRoot,
            eventSub.Transport,
            chat.Value.Enabled);

    private void LogStopping() =>
        logger.LogInformation("Stream Keepr stopping: host shutdown requested.");
}
