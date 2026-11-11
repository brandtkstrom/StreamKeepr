using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>Routes one channel observation to its coordinator.</summary>
public delegate ValueTask StreamObservationRoute(
    string login, StreamObservation observation, CancellationToken ct);

/// <summary>
/// Produces liveness observations for the watched channels. Implementations must translate
/// Twitch/API failures into <see cref="StreamObservation.Unknown"/> instead of throwing; an
/// <c>Unknown</c> observation never finalizes a recording. The fake poller in tests and the
/// Mock profile implement this seam instead of calling Twitch.
/// </summary>
public interface IStreamPoller
{
    Task RunAsync(
        IReadOnlyList<string> logins,
        StreamObservationRoute route,
        CancellationToken ct);
}
