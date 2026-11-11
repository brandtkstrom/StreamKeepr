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

    public async Task RunAsync(
        IReadOnlyList<string> logins,
        StreamObservationRoute route,
        CancellationToken ct)
    {
        _started.TrySetResult(logins);

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

    public void Send(string login, StreamObservation observation) =>
        _observations.Writer.TryWrite((login, observation));
}
