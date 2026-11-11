using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Runtime;

public sealed class ShutdownParallelismTests
{
    private const string FirstChannel = "alpha";
    private const string SecondChannel = "beta";

    private static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Active_jobs_stop_in_parallel_during_host_shutdown()
    {
        var recorder = new SlowStopRecorder();
        var poller = new ScriptedStreamPoller();
        var overrides = new Dictionary<string, string?>
        {
            [$"{TwitchOptions.SectionName}:Channels:0"] = FirstChannel,
            [$"{TwitchOptions.SectionName}:Channels:1"] = SecondChannel,
            [$"{StorageOptions.SectionName}:MinFreeGb"] = "1",
        };

        using var factory = new StreamKeeprAppFactory(
            overrides,
            configureTestServices: services =>
            {
                services.AddSingleton<IRecorder>(recorder);
                services.AddSingleton<IStreamPoller>(poller);
            });
        using var client = factory.CreateClient();

        await poller.Started.WaitAsync(TimeSpan.FromSeconds(10));
        poller.Send(FirstChannel, new StreamObservation.Live(
            new LiveStream("111", "First", "Just Chatting", StartedAt, null)));
        poller.Send(SecondChannel, new StreamObservation.Live(
            new LiveStream("222", "Second", "Just Chatting", StartedAt, null)));

        await recorder.BothStarted.WaitAsync(TimeSpan.FromSeconds(10));

        var stopwatch = Stopwatch.StartNew();
        factory.Dispose();
        stopwatch.Stop();

        Assert.Equal(2, recorder.MaxConcurrentStops);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"shutdown took {stopwatch.Elapsed}; expected both jobs to stop in parallel");
    }

    private sealed class SlowStopRecorder : IRecorder
    {
        private static readonly TimeSpan StopDelay = TimeSpan.FromMilliseconds(750);

        private readonly TaskCompletionSource _bothStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _active;
        private int _maxConcurrent;

        public Task BothStarted => _bothStarted.Task;

        public int MaxConcurrentStops => Volatile.Read(ref _maxConcurrent);

        public async Task RunAsync(
            Session session,
            JobId job,
            Func<DvrEvent, CancellationToken, ValueTask> publish,
            CancellationToken stopRequested = default,
            Action<JobId, long, TimeSpan>? reportProgress = null)
        {
            if (Interlocked.Increment(ref _started) == 2)
                _bothStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopRequested);
            }
            catch (OperationCanceledException) when (stopRequested.IsCancellationRequested)
            {
            }

            int active = Interlocked.Increment(ref _active);
            RecordMaximum(active);
            await Task.Delay(StopDelay);
            Interlocked.Decrement(ref _active);

            await publish(
                new DvrEvent.RecorderExited(
                    job, DateTimeOffset.UtcNow, RecorderExitKind.Stopped, "stopped"),
                CancellationToken.None);
        }

        private void RecordMaximum(int active)
        {
            int current;
            while (active > (current = Volatile.Read(ref _maxConcurrent))
                && Interlocked.CompareExchange(ref _maxConcurrent, active, current) != current)
            {
            }
        }
    }
}
