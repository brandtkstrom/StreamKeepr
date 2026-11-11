using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Runtime;

/// <summary>Builds one coordinator per channel with the validated recorder and storage options.</summary>
public sealed class ChannelCoordinatorFactory(
    IOptions<RecorderOptions> recorderOptions,
    IOptions<StorageOptions> storage,
    IRecorder recorder,
    SessionStore store,
    DvrStatus status,
    IRetryJitter jitter,
    TimeProvider clock,
    ILoggerFactory loggers)
{
    public ChannelCoordinator Create(string channel, DvrState initialState) =>
        new(
            channel,
            DvrSettingsFactory.Create(channel, recorderOptions.Value, storage.Value),
            initialState,
            recorder,
            store,
            status,
            jitter,
            clock,
            loggers.CreateLogger($"StreamKeepr.Channel.{channel}"));
}
