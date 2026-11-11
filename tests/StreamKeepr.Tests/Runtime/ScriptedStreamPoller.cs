using System.Threading.Channels;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Runtime;

/// <summary>A poller the test drives directly, so recording does not wait on a real interval.</summary>
internal sealed class ScriptedStreamPoller : IStreamPoller
{
    private readonly Channel<(string Login, StreamObservation Observation)> _observations =
        Channel.CreateUnbounded<(string, StreamObservation)>();

    private readonly TaskCompletionSource<IReadOnlyList<string>> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<string>> Started => _started.Task;

    public Dictionary<string, ChannelProfile> Profiles { get; } =
        new(StringComparer.Ordinal);

    private int _nudges;

    public int Nudges => Volatile.Read(ref _nudges);

    public async Task RunAsync(
        Func<IReadOnlyList<string>> logins,
        StreamObservationRoute route,
        CancellationToken ct)
    {
        _started.TrySetResult(logins());

        try
        {
            await foreach ((string login, StreamObservation observation) in
                _observations.Reader.ReadAllAsync(ct))
            {
                await route(login, observation, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public Task<ChannelProfile?> GetUserAsync(string login, CancellationToken ct) =>
        Task.FromResult(Profiles.GetValueOrDefault(login));

    public void Nudge() => Interlocked.Increment(ref _nudges);

    public void Send(string login, StreamObservation observation) =>
        _observations.Writer.TryWrite((login, observation));
}
