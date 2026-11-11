using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Runtime;

/// <summary>
/// Owns one channel's single-reader event loop. It is the only writer of the reducer state and
/// executes the reducer's commands sequentially; producers only enqueue events.
/// </summary>
public sealed class ChannelCoordinator
{
    /// <summary>Worst-case recorder shutdown (20 s) plus the finalize cap (15 s).</summary>
    public static readonly TimeSpan JobStopBudget = TimeSpan.FromSeconds(35);

    private static readonly TimeSpan MaxTimerDelay =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly Channel<DvrEvent> _events = Channel.CreateUnbounded<DvrEvent>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly string _channel;
    private readonly DvrSettings _settings;
    private readonly IRecorder _recorder;
    private readonly SessionStore _store;
    private readonly DiskGuard _disk;
    private readonly DvrStatus _status;
    private readonly IRetryJitter _jitter;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    private DvrState _state;
    private ActiveJob? _job;
    private RetryTimer? _retry;
    private int _queueDepth;

    public ChannelCoordinator(
        string channel,
        DvrSettings settings,
        DvrState initialState,
        IRecorder recorder,
        SessionStore store,
        DiskGuard disk,
        DvrStatus status,
        IRetryJitter jitter,
        TimeProvider clock,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(jitter);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _channel = channel;
        _settings = settings;
        _state = initialState;
        _recorder = recorder;
        _store = store;
        _disk = disk;
        _status = status;
        _jitter = jitter;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues one event. Never blocks and never throws; after the loop completes, late events
    /// are dropped. Unbounded is intentional: the loop may await a job stop while the job's own
    /// output callback is publishing.
    /// </summary>
    public ValueTask PublishAsync(DvrEvent @event, CancellationToken ct = default)
    {
        if (!_events.Writer.TryWrite(@event))
            return ValueTask.CompletedTask;

        int depth = Interlocked.Increment(ref _queueDepth);
        if (depth > 1000 && depth % 1000 == 0)
        {
            _logger.LogWarning(
                "{Channel}: event queue depth is {Depth}; the coordinator is falling behind.",
                _channel,
                depth);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Lets the loop drain queued events and then end; used when a channel is removed.</summary>
    public void Complete() => _events.Writer.TryComplete();

    public async Task RunAsync(CancellationToken ct)
    {
        _status.Applied(_channel, null, _state, _clock.GetUtcNow());

        try
        {
            await foreach (DvrEvent @event in _events.Reader.ReadAllAsync(ct))
            {
                Interlocked.Decrement(ref _queueDepth);

                DvrState before = _state;
                Step step = DvrReducer.Transition(before, @event, _settings);
                _state = step.Next;
                _status.Applied(_channel, @event, step.Next, _clock.GetUtcNow());

                if (before.GetType() != step.Next.GetType())
                {
                    _logger.LogInformation(
                        "{Channel}: {From} -> {To} on {Event}",
                        _channel,
                        before.GetType().Name,
                        step.Next.GetType().Name,
                        @event.GetType().Name);
                }

                // A retry timer belongs only to the WaitingToRetry state that scheduled it.
                if (_retry is { } retry
                    && !(_state is DvrState.WaitingToRetry waiting && waiting.Next == retry.Job))
                {
                    await CancelRetryAsync();
                }

                await ExecuteCommandsAsync(step.Commands, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown; the finally block stops the active part.
        }
        finally
        {
            _events.Writer.TryComplete();
            await CancelRetryAsync();
            await StopJobAsync(fatalOnTimeout: false);
        }
    }

    private async Task ExecuteCommandsAsync(ImmutableArray<DvrCommand> commands, CancellationToken ct)
    {
        // A failed open must suppress the StartRecorder that follows it in the same step.
        bool skipRecorder = false;

        foreach (DvrCommand command in commands)
        {
            switch (command)
            {
                case DvrCommand.OpenSession open:
                    skipRecorder = await TrySessionCommandAsync(
                        () => _store.OpenAsync(open.Session, open.Stream, ct), "open", ct);
                    break;

                case DvrCommand.ReopenSession reopen:
                    skipRecorder = await TrySessionCommandAsync(
                        () => _store.ReopenAsync(reopen.Session, ct), "reopen", ct);
                    break;

                case DvrCommand.RecordMetadata metadata:
                    await TrySessionCommandAsync(
                        () => _store.AppendMetadataAsync(metadata.Session, metadata.Stream, ct),
                        "metadata",
                        ct);
                    break;

                case DvrCommand.FinalizeSession finalize:
                    await TrySessionCommandAsync(
                        () => _store.FinalizeAsync(finalize.Session, ct), "finalize", ct);
                    break;

                case DvrCommand.StopRecorder stop when _job?.Id == stop.Job:
                    await StopJobAsync(fatalOnTimeout: true);
                    break;

                case DvrCommand.StopRecorder:
                    break; // A stale stop for a job that already ended.

                case DvrCommand.StartRecorder start:
                    if (skipRecorder)
                    {
                        skipRecorder = false;
                        await PublishAsync(new DvrEvent.RecorderExited(
                            start.Job, _clock.GetUtcNow(), RecorderExitKind.Exited, "open failed"));
                        break;
                    }

                    await StartJobAsync(start.Session, start.Job, ct);
                    break;

                case DvrCommand.ScheduleRetry retry:
                    await ScheduleRetryAsync(retry.Job, retry.BaseDelay, ct);
                    break;
            }
        }
    }

    /// <summary>
    /// Runs one manifest mutation. Failures are logged and surfaced, never fatal: the reducer
    /// retries cleanly, and recovery repairs an incomplete finalize on the next start.
    /// </summary>
    private async Task<bool> TrySessionCommandAsync(
        Func<Task> command, string name, CancellationToken ct)
    {
        try
        {
            await command();
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "{Channel}: {Command} failed", _channel, name);
            _status.Failed(_channel, ex.Message);
            return true;
        }
    }

    private async Task StartJobAsync(Session session, JobId job, CancellationToken ct)
    {
        // Never start a second writer while the previous job may still be writing.
        await StopJobAsync(fatalOnTimeout: true);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task completion = Task.Run(() => RecordPartAsync(session, job, cts.Token), CancellationToken.None);
        _job = new ActiveJob(job, cts, completion);
    }

    private async Task RecordPartAsync(Session session, JobId job, CancellationToken stopRequested)
    {
        try
        {
            // The disk probe is inside the failure boundary: an I/O error here must still
            // become one visible RecorderExited instead of a dead job with no event.
            if (!_disk.HasRoomToRecord())
            {
                await FailPartAsync(job, "not enough free disk space");
                return;
            }

            await _recorder.RunAsync(
                session,
                job,
                PublishAsync,
                stopRequested,
                ReportProgress);
        }
        catch (OperationCanceledException) when (stopRequested.IsCancellationRequested)
        {
            await PublishAsync(new DvrEvent.RecorderExited(
                job, _clock.GetUtcNow(), RecorderExitKind.Stopped, "recorder canceled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Channel}: recorder {Job} failed", _channel, job);
            await FailPartAsync(job, ex.Message);
        }
    }

    private async Task FailPartAsync(JobId job, string detail)
    {
        _status.Failed(_channel, detail);
        await PublishAsync(new DvrEvent.RecorderExited(
            job, _clock.GetUtcNow(), RecorderExitKind.Exited, detail));
    }

    private void ReportProgress(JobId job, long bytes, TimeSpan mediaTime) =>
        _status.ReportProgress(_channel, job, bytes, mediaTime, _clock.GetUtcNow());

    private async Task StopJobAsync(bool fatalOnTimeout)
    {
        if (_job is not { } job)
            return;

        _job = null;
        await job.Cts.CancelAsync();

        using var budgetCts = new CancellationTokenSource();
        Task budget = Task.Delay(JobStopBudget, _clock, budgetCts.Token);
        Task finished = await Task.WhenAny(job.Completion, budget);
        if (finished != job.Completion)
        {
            _logger.LogError(
                "{Channel}: recorder {Job} did not stop within {Budget}.",
                _channel,
                job.Id,
                JobStopBudget);

            if (fatalOnTimeout)
            {
                throw new ChannelFatalException(
                    $"Recorder {job.Id} did not stop within {JobStopBudget}; refusing to start another writer.");
            }

            return;
        }

        await budgetCts.CancelAsync();
        await job.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        job.Cts.Dispose();
    }

    private async Task ScheduleRetryAsync(JobId job, TimeSpan baseDelay, CancellationToken ct)
    {
        // The previous part is over, but its task may still be unwinding. Never leave two
        // writers or a stale retry timer behind.
        await StopJobAsync(fatalOnTimeout: true);
        await CancelRetryAsync();

        TimeSpan delay;
        try
        {
            delay = _jitter.Apply(baseDelay);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ChannelFatalException($"Could not schedule a retry for {job}.", ex);
        }

        if (delay <= TimeSpan.Zero || delay > MaxTimerDelay)
        {
            throw new ChannelFatalException(
                $"Retry delay {delay} for {job} is outside the timer range.");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Start the delay directly (not via Task.Run) so the timer is registered before this
        // command returns; FakeTimeProvider tests can advance it deterministically.
        Task completion = DelayAndPublishAsync(job, delay, cts);
        _retry = new RetryTimer(job, cts, completion);
        _logger.LogInformation(
            "{Channel}: retrying {Job} in {DelaySeconds:F1} s",
            _channel,
            job,
            delay.TotalSeconds);
    }

    private async Task DelayAndPublishAsync(
        JobId job, TimeSpan delay, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delay, _clock, cts.Token);
            await PublishAsync(new DvrEvent.RetryTimerElapsed(job));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Stopped, resumed, or replaced before the timer elapsed.
        }
    }

    private async Task CancelRetryAsync()
    {
        if (_retry is not { } retry)
            return;

        _retry = null;
        await retry.Cts.CancelAsync();
        await retry.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        retry.Cts.Dispose();
    }

    private sealed record ActiveJob(JobId Id, CancellationTokenSource Cts, Task Completion);

    private sealed record RetryTimer(JobId Job, CancellationTokenSource Cts, Task Completion);
}

/// <summary>A channel loop fault that must stop the host rather than risk a second writer.</summary>
public sealed class ChannelFatalException : Exception
{
    public ChannelFatalException(string message) : base(message)
    {
    }

    public ChannelFatalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
