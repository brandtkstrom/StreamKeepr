namespace StreamKeepr.Domain;

/// <summary>A Twitch liveness observation.</summary>
public abstract record StreamObservation
{
    private StreamObservation() { }

    public sealed record Live(LiveStream Stream) : StreamObservation;

    public sealed record Offline : StreamObservation;

    /// <summary>An API failure. Unknown is never treated as offline.</summary>
    public sealed record Unknown(string Error) : StreamObservation;
}
