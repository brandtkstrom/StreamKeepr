namespace StreamKeepr.Domain;

/// <summary>A session plus the next part number that can be resumed or reopened.</summary>
/// <param name="Complete">
/// The manifest is already complete and must be reopened before the next part.
/// </param>
public sealed record Resumable(Session Session, int NextPart, bool Complete = false);
