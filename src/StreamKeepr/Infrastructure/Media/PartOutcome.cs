using StreamKeepr.Domain;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// The result of one Job, built on every path (glossary: Part outcome). The coordinator publishes
/// exactly one <see cref="DvrEvent.RecorderExited"/> from it; the job has already counted the part
/// and logged its finish when tools ran.
/// </summary>
/// <param name="Kind">The Recorder exit kind; always <see cref="RecorderExitKind.Killed"/> when <paramref name="Fault"/> is set.</param>
/// <param name="Detail">Redacted diagnostic text; the reducer never parses it.</param>
/// <param name="Bytes">The part's size on disk, or null when it could not be read or no tool ran.</param>
/// <param name="Duration">How long the tools ran; zero when none started.</param>
/// <param name="Failed">True when the part ended on an error that becomes the channel's last error.</param>
/// <param name="Fault">Set for an unverified shutdown: the writer may still be alive and the channel must stop.</param>
public sealed record PartOutcome(
    RecorderExitKind Kind,
    string Detail,
    long? Bytes = null,
    TimeSpan Duration = default,
    bool Failed = false,
    UnverifiedShutdown? Fault = null)
{
    public RecorderExitKind Kind { get; init; } =
        Fault is null || Kind == RecorderExitKind.Killed
            ? Kind
            : throw new ArgumentException("An unverified shutdown is always Killed.", nameof(Kind));
}
