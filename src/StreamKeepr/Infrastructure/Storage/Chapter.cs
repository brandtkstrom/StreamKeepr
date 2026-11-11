namespace StreamKeepr.Infrastructure.Storage;

/// <summary>A title/game change positioned on the stitched playlist timeline.</summary>
public sealed record Chapter(double StartSeconds, string Title, string Game);
