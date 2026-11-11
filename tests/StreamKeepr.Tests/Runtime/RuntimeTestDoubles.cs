using System.Threading.Channels;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Runtime;

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

    public static async Task<T> ForValueAsync<T>(Func<T?> probe, TimeSpan timeout)
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

    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException($"Condition was not met within {timeout}.");
    }
}
