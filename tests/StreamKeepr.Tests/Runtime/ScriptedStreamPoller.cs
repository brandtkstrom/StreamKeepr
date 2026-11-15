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
    private int _lookups;

    public int Nudges => Volatile.Read(ref _nudges);

    public int Lookups => Volatile.Read(ref _lookups);

    /// <summary>Overrides the default profile lookup when a test needs failures or delays.</summary>
    public Func<string, CancellationToken, Task<ChannelProfile?>>? Lookup { get; set; }

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

    public Task<ChannelProfile?> GetUserAsync(string login, CancellationToken ct)
    {
        Interlocked.Increment(ref _lookups);
        return Lookup is null
            ? Task.FromResult(Profiles.GetValueOrDefault(login))
            : Lookup(login, ct);
    }

    public Task<ChannelInfo?> GetChannelInfoAsync(
        string broadcasterId, CancellationToken ct) =>
        Task.FromResult<ChannelInfo?>(null);

    public void Nudge() => Interlocked.Increment(ref _nudges);

    public void Send(string login, StreamObservation observation) =>
        _observations.Writer.TryWrite((login, observation));
}
