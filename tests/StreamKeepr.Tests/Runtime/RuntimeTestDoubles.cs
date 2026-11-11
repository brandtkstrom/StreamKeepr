using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Runtime;

internal sealed class FakeRecorder : IRecorder
{
    private readonly Channel<RecorderCall> _started = Channel.CreateUnbounded<RecorderCall>();

    public Func<RecorderCall, CancellationToken, Task>? OnRun { get; set; }

    public ChannelReader<RecorderCall> Started => _started.Reader;

    public async Task RunAsync(
        Session session,
        JobId job,
        Func<DvrEvent, CancellationToken, ValueTask> publish,
        CancellationToken stopRequested = default,
        Action<JobId, long, TimeSpan>? reportProgress = null)
    {
        var call = new RecorderCall(session, job, publish, reportProgress, stopRequested);
        _started.Writer.TryWrite(call);

        if (OnRun is { } onRun)
            await onRun(call, stopRequested);
    }
}

internal sealed class RecorderCall(
    Session session,
    JobId job,
    Func<DvrEvent, CancellationToken, ValueTask> publish,
    Action<JobId, long, TimeSpan>? reportProgress,
    CancellationToken stopRequested)
{
    public Session Session { get; } = session;

    public JobId Job { get; } = job;

    public Action<JobId, long, TimeSpan>? ReportProgress { get; } = reportProgress;

    public CancellationToken StopRequested { get; } = stopRequested;

    public ValueTask PublishAsync(DvrEvent @event) => publish(@event, CancellationToken.None);
}

internal sealed class FixedRetryJitter(TimeSpan delay) : IRetryJitter
{
    private readonly TaskCompletionSource<TimeSpan> _applied =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<TimeSpan> Applied => _applied.Task;

    public TimeSpan Apply(TimeSpan baseDelay)
    {
        _applied.TrySetResult(baseDelay);
        return delay;
    }
}

/// <summary>
/// Wraps a fake clock and tracks timers created through <see cref="TimeProvider.CreateTimer"/>,
/// so tests can wait for a timer to be registered instead of sleeping.
/// </summary>
internal sealed class TrackingTimeProvider : TimeProvider
{
    private readonly FakeTimeProvider _inner;
    private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();
    private int _active;

    public TrackingTimeProvider(DateTimeOffset now) => _inner = new FakeTimeProvider(now);

    public int ActiveTimers => Volatile.Read(ref _active);

    public ChannelReader<TimeSpan> CreatedTimers => _created.Reader;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _active);
        _created.Writer.TryWrite(dueTime);

        int completed = 0;
        void Complete()
        {
            if (Interlocked.Exchange(ref completed, 1) == 0)
                Interlocked.Decrement(ref _active);
        }

        ITimer timer = _inner.CreateTimer(
            _ =>
            {
                Complete();
                callback(state);
            },
            state,
            dueTime,
            period);

        return new TrackedTimer(timer, Complete);
    }

    public void Advance(TimeSpan time) => _inner.Advance(time);

    private sealed class TrackedTimer(ITimer inner, Action complete) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            inner.Change(dueTime, period);

        public void Dispose()
        {
            complete();
            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class ThrowingDiskSpace : IDiskSpace
{
    public long AvailableFreeSpace(string path) =>
        throw new IOException("disk probe failed");

    public long TotalSize(string path) =>
        throw new IOException("disk probe failed");
}

internal sealed class ThrowingRetryJitter : IRetryJitter
{
    public TimeSpan Apply(TimeSpan baseDelay) =>
        throw new InvalidOperationException("retry scheduling is unavailable");
}

internal sealed class InvalidDelayRetryJitter : IRetryJitter
{
    public TimeSpan Apply(TimeSpan baseDelay) => TimeSpan.MaxValue;
}

internal sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _stopRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => _stopping.Token;

    public Task StopRequested => _stopRequested.Task;

    public void StopApplication()
    {
        _stopRequested.TrySetResult();
        _stopping.Cancel();
    }
}

internal static class RuntimeTestWait
{
    public static async Task<DvrSnapshot> ForStatusAsync(
        DvrStatus status,
        string channel,
        Func<DvrSnapshot, bool> predicate)
    {
        var ready = new TaskCompletionSource<DvrSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Check()
        {
            if (status.Current.TryGetValue(channel, out DvrSnapshot? snapshot) && predicate(snapshot))
                ready.TrySetResult(snapshot);
        }

        status.Changed += Check;
        Check();

        try
        {
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            status.Changed -= Check;
        }
    }
}
