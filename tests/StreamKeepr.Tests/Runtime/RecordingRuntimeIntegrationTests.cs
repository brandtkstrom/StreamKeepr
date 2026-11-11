using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Runtime;

public sealed class RecordingRuntimeIntegrationTests
{
    private const string Channel = "streamer_one";

    private static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    private static readonly LiveStream Live = new(
        "987654321", "Tracer stream", "Just Chatting", StartedAt, ThumbnailUrl: null);

    [Fact]
    public async Task A_configured_channel_records_and_serves_the_finalized_recording_through_media()
    {
        string ffmpeg = TestPaths.RequireExecutable(
            "ffmpeg",
            "ffmpeg is required for the runtime integration test. Install ffmpeg and put it on PATH.");

        var poller = new ScriptedStreamPoller();
        var overrides = new Dictionary<string, string?>
        {
            [$"{TwitchOptions.SectionName}:Channels:0"] = Channel,
            [$"{RecorderOptions.SectionName}:StreamlinkPath"] = TestPaths.FixturePath("fake-streamlink"),
            [$"{RecorderOptions.SectionName}:FfmpegPath"] = ffmpeg,
            [$"{RecorderOptions.SectionName}:SegmentSeconds"] = "2",
            [$"{RecorderOptions.SectionName}:FinalizeTimeoutSeconds"] = "5",
            [$"{RecorderOptions.SectionName}:StallTimeoutSeconds"] = "30",
            // Keep the retry timer far away so the two offline observations win the race.
            [$"{RecorderOptions.SectionName}:RetryBaseSeconds"] = "60",
            [$"{RecorderOptions.SectionName}:RetryMaxSeconds"] = "600",
            [$"{StorageOptions.SectionName}:MinFreeGb"] = "1",
        };

        using var factory = new StreamKeeprAppFactory(
            overrides,
            configureTestServices: services => services.AddSingleton<IStreamPoller>(poller));
        using var client = factory.CreateClient();

        IReadOnlyList<string> logins = await poller.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([Channel], logins);

        DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();
        RecordingLibrary library = factory.Services.GetRequiredService<RecordingLibrary>();
        var recordingSeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingSeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        status.Changed += () =>
        {
            if (!status.Current.TryGetValue(Channel, out DvrSnapshot? snapshot))
                return;

            if (snapshot.State is DvrState.Recording)
                recordingSeen.TrySetResult();
            if (snapshot.State is DvrState.WaitingToRetry)
                waitingSeen.TrySetResult();
        };

        poller.Send(Channel, new StreamObservation.Live(Live));
        await recordingSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The short fake source ends on its own; only then can offline observations finalize.
        await waitingSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var waiting = Assert.IsType<DvrState.WaitingToRetry>(
            status.Current[Channel].State);
        Assert.True(
            File.Exists(Path.Combine(waiting.Session.Directory, "p001.m3u8")),
            "the real Streamlink/FFmpeg pipeline did not write a part playlist");
        Assert.NotEmpty(Directory.GetFiles(waiting.Session.Directory, "p001_*.m4s"));

        poller.Send(Channel, new StreamObservation.Offline());
        poller.Send(Channel, new StreamObservation.Offline());

        RecordingSummary summary = await WaitForAsync(
            () =>
            {
                IReadOnlyList<RecordingSummary> all = library.All;
                return all.Count == 1 && !all[0].IsRecording ? all[0] : null;
            },
            TimeSpan.FromSeconds(30));

        string indexPath = $"/media/{Channel}/{summary.Id}/index.m3u8";
        using HttpResponseMessage indexResponse = await client.GetAsync(indexPath);

        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        Assert.Equal(
            RecordingsMedia.PlaylistContentType,
            indexResponse.Content.Headers.ContentType?.MediaType);

        string index = await indexResponse.Content.ReadAsStringAsync();
        Assert.Contains("#EXT-X-ENDLIST", index, StringComparison.Ordinal);

        string segment = index
            .Split('\n')
            .Select(line => line.Trim())
            .First(line => line.Length > 0
                && line[0] != '#'
                && line.EndsWith(".m4s", StringComparison.Ordinal));

        using HttpResponseMessage segmentResponse =
            await client.GetAsync($"/media/{Channel}/{summary.Id}/{segment}");

        Assert.Equal(HttpStatusCode.OK, segmentResponse.StatusCode);
        Assert.Equal("video/iso.segment", segmentResponse.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await segmentResponse.Content.ReadAsByteArrayAsync());
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout)
        where T : class
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (probe() is { } value)
                return value;

            await Task.Delay(25);
        }

        throw new TimeoutException($"Condition was not met within {timeout}.");
    }
}
