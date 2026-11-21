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
    /// <summary>The shortest gap between two viewer samples of one session, by observation time.</summary>
    public static readonly TimeSpan ViewerSampleInterval = TimeSpan.FromSeconds(60);

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
    private ViewerSampleMark? _lastViewerSample;
    private ActiveJob? _job;
    private RetryTimer? _retry;
    private int _queueDepth;

    // Durable once set: the active job's writer may still be alive, so no later StartJob,
    // StopJob, or ScheduleRetry may start another one, and the loop must fault. The job task sets
    // it for an unverified shutdown; the loop sets it when a stop overruns its budget.
    private WriterHazard? _hazard;
    private readonly TaskCompletionSource _faultSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    /// <summary>The active job, retained after an unverified shutdown.</summary>
    internal JobId? ActiveJobId => _job?.Id;

    /// <summary>
    /// Fails the channel loop when the active job's writer may still be alive. The active job is
    /// retained, so no command may start another writer.
    /// </summary>
    private void ThrowIfWriterHazard()
    {
        if (Volatile.Read(ref _hazard) is { } hazard)
            throw new ChannelFatalException($"{_channel}: {hazard.Describe()}; refusing to start another writer.");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _status.Applied(_channel, null, _state, _clock.GetUtcNow());

        Task<DvrEvent>? pending = null;
        try
        {
            while (true)
            {
                pending = _events.Reader.ReadAsync(ct).AsTask();

                // Race the fault signal with the queue so a stop that cannot be verified fails
                // the loop immediately, even when the reducer enters CoolingDown and emits no
                // command that would otherwise notice it.
                Task first = await Task.WhenAny(pending, _faultSignal.Task)
                    .ConfigureAwait(false);
                if (first == _faultSignal.Task)
                    ThrowIfWriterHazard();

                DvrEvent @event;
                try
                {
                    @event = await pending.ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    pending = null;
                    break;
                }

                pending = null;
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
                ThrowIfWriterHazard();
                await SampleViewersAsync(@event, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown; the finally block stops the active part.
        }
        finally
        {
            _events.Writer.TryComplete();
            if (pending is not null)
            {
                // Observe the read that was still pending when the fault signal ended the loop.
                try
                {
                    await pending.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The host token was canceled while the loop was unwinding.
                }
                catch (ChannelClosedException)
                {
                    // The writer was completed as the loop ended.
                }
            }
            await CancelRetryAsync();
            await StopJobCoreAsync(throwOnHazard: false);
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
                    await StopJobAsync();
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

    /// <summary>
    /// Samples the viewer count of a counted <c>Live</c> observation for the open session, at most
    /// once per <see cref="ViewerSampleInterval"/> of observation time. Unknown, offline, and
    /// uncounted observations add nothing, so a gap stays a gap. Sampling never fails recording:
    /// a write failure logs one warning and is otherwise ignored.
    /// </summary>
    private async Task SampleViewersAsync(DvrEvent @event, CancellationToken ct)
    {
        if (@event is not DvrEvent.StreamStatusObserved
            {
                Observation: StreamObservation.Live { Stream: { ViewerCount: int count } live },
            } observed)
        {
            return;
        }

        // WaitingToRetry is a reconnecting session: still open, so it keeps sampling.
        Session? session = _state switch
        {
            DvrState.Recording recording => recording.Session,
            DvrState.WaitingToRetry waiting => waiting.Session,
            _ => null,
        };
        if (session is null || session.StreamId != live.StreamId)
            return;

        if (_lastViewerSample is { } last
            && last.SessionId == session.Id
            && observed.At - last.At < ViewerSampleInterval)
        {
            return;
        }

        _lastViewerSample = new ViewerSampleMark(session.Id, observed.At);
        try
        {
            await _store.AppendViewerSampleAsync(session, observed.At, count, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex, "{Channel}: viewer sample for {Session} was not written.", _channel, session.Id);
        }
    }

    private async Task StartJobAsync(Session session, JobId job, CancellationToken ct)
    {
        // Never start a second writer while the previous job may still be writing.
        await StopJobAsync();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task completion = Task.Run(() => RecordPartAsync(session, job, cts.Token), CancellationToken.None);
        _job = new ActiveJob(job, cts, completion);
    }

    private async Task RecordPartAsync(Session session, JobId job, CancellationToken stopRequested)
    {
        PartOutcome outcome;
        try
        {
            // The disk probe is inside the failure boundary: an I/O error here must still
            // become one visible RecorderExited instead of a dead job with no event.
            outcome = _disk.HasRoomToRecord()
                ? await _recorder.RunAsync(session, job, PublishAsync, stopRequested, ReportProgress)
                : new PartOutcome(RecorderExitKind.Exited, "not enough free disk space", Failed: true);
        }
        catch (OperationCanceledException) when (stopRequested.IsCancellationRequested)
        {
            outcome = new PartOutcome(RecorderExitKind.Stopped, "recorder canceled");
        }
        catch (Exception ex)
        {
            // The recorder reports every failure after its tools start; this covers a failure
            // before any tool ran, so there is no writer to verify.
            _logger.LogError(ex, "{Channel}: recorder {Job} failed", _channel, job);
            outcome = new PartOutcome(RecorderExitKind.Exited, ex.Message, Failed: true);
        }

        await CompletePartAsync(job, outcome);
    }

    /// <summary>
    /// The single place a part's <see cref="DvrEvent.RecorderExited"/> is published. An unverified
    /// shutdown publishes one Killed exit for status and streak, makes the fault durable, and
    /// fails the job task so the active job is retained and no second writer can start.
    /// </summary>
    private async Task CompletePartAsync(JobId job, PartOutcome outcome)
    {
        if (outcome.Fault is { } shutdown)
        {
            Volatile.Write(ref _hazard, new WriterHazard.ShutdownUnverified(job, shutdown));
            _logger.LogError(
                "{Channel}: recorder {Job} shutdown could not be verified: {Fault}",
                _channel,
                job,
                shutdown);
            _status.Failed(_channel, shutdown.ToString());
        }
        else if (outcome.Failed)
        {
            _status.Failed(_channel, outcome.Detail);
        }

        await PublishAsync(new DvrEvent.RecorderExited(
            job, _clock.GetUtcNow(), outcome.Kind, outcome.Detail));

        if (outcome.Fault is not null)
        {
            _faultSignal.TrySetResult();
            ThrowIfWriterHazard();
        }
    }

    private void ReportProgress(JobId job, long bytes, TimeSpan mediaTime) =>
        _status.ReportProgress(_channel, job, bytes, mediaTime, _clock.GetUtcNow());

    /// <summary>Stops the active job and fails the loop if its writer may still be alive.</summary>
    private async Task StopJobAsync()
    {
        ThrowIfWriterHazard();
        await StopJobCoreAsync(throwOnHazard: true);
        ThrowIfWriterHazard();
    }

    /// <summary>
    /// Stops the active job within the recorder's stop budget. An overrun records a durable
    /// <see cref="WriterHazard.StopBudgetExceeded"/>. Only the loop's finally block passes
    /// <paramref name="throwOnHazard"/> false: it is already ending and must not mask its cause.
    /// </summary>
    private async Task StopJobCoreAsync(bool throwOnHazard)
    {
        if (_job is not { } job)
            return;

        // A job behind a hazard is abandoned in place; never spend a second budget on it.
        if (Volatile.Read(ref _hazard) is not null)
        {
            if (throwOnHazard)
                ThrowIfWriterHazard();
            return;
        }

        if (!job.Completion.IsCompleted)
            await job.Cts.CancelAsync();

        TimeSpan stopBudget = _recorder.StopBudget;
        using var budgetCts = new CancellationTokenSource();
        Task budget = Task.Delay(stopBudget, _clock, budgetCts.Token);
        Task finished = await Task.WhenAny(job.Completion, budget);
        if (finished != job.Completion)
        {
            Volatile.Write(ref _hazard, new WriterHazard.StopBudgetExceeded(job.Id, stopBudget));
            _logger.LogError(
                "{Channel}: recorder {Job} did not stop within {Budget}.",
                _channel,
                job.Id,
                stopBudget);

            if (throwOnHazard)
                ThrowIfWriterHazard();
            return;
        }

        await budgetCts.CancelAsync();
        await job.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        job.Cts.Dispose();

        // Only a successful completion proves the recorder returned on its own terms. A faulted
        // shutdown keeps the active job so no later command can start a second writer.
        if (job.Completion.Status == TaskStatus.RanToCompletion)
            _job = null;
    }

    private async Task ScheduleRetryAsync(JobId job, TimeSpan baseDelay, CancellationToken ct)
    {
        ThrowIfWriterHazard();

        // The previous part is over, but its task may still be unwinding. Never leave two
        // writers or a stale retry timer behind.
        await StopJobAsync();
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

    private sealed record ViewerSampleMark(string SessionId, DateTimeOffset At);
}

/// <summary>A channel loop fault that must stop the host rather than risk a second writer.</summary>
/// <summary>
/// Why a channel's active Job may still have a live writer. Every cause is fatal for the channel
/// loop and blocks any later writer; a new cause is a new case here, not a new flag.
/// </summary>
internal abstract record WriterHazard(JobId Job)
{
    public abstract string Describe();

    /// <summary>The recorder could not prove its tools stopped.</summary>
    public sealed record ShutdownUnverified(JobId Job, UnverifiedShutdown Shutdown) : WriterHazard(Job)
    {
        public override string Describe() =>
            $"recorder {Job} shutdown could not be verified "
            + $"({Shutdown.Tool}, pid {Shutdown.ProcessId?.ToString() ?? "unknown"})";
    }

    /// <summary>The recorder did not return within its own stop budget.</summary>
    public sealed record StopBudgetExceeded(JobId Job, TimeSpan Budget) : WriterHazard(Job)
    {
        public override string Describe() => $"recorder {Job} did not stop within {Budget}";
    }
}

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
