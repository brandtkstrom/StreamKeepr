namespace StreamKeepr.Infrastructure.Storage;

/// <summary>
/// A title/game observation recorded during a session. The first entry is the title at stream
/// start; later entries become chapters on the stitched timeline.
/// </summary>
public sealed record MetadataChange(DateTimeOffset At, string Title, string Game);
