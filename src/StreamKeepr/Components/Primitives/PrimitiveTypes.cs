namespace StreamKeepr.Components.Primitives;

public enum AvatarTone
{
    Default,
    Primary,
    Tertiary,
    Secondary,
}

/// <summary>
/// Recording conditions shown by <see cref="StatusChip"/>. <c>Reconnecting</c>
/// and <c>Partial</c> share the warning treatment; the label distinguishes them.
/// </summary>
public enum StatusChipKind
{
    Rec,
    Reconnecting,
    Processing,
    Partial,
    Saved,
    Error,

    /// <summary>Settings only: healthy service, a live push connection, full coverage.</summary>
    Healthy,
}

/// <summary>
/// The chip a recording's state shows, wherever recordings are listed or detailed; null for
/// a saved recording, which shows none.
/// </summary>
public static class RecordingStatusChip
{
    public static StatusChipKind? For(StreamKeepr.Ui.RecordingState state) => state switch
    {
        StreamKeepr.Ui.RecordingState.Recording => StatusChipKind.Rec,
        StreamKeepr.Ui.RecordingState.Reconnecting => StatusChipKind.Reconnecting,
        StreamKeepr.Ui.RecordingState.Partial => StatusChipKind.Partial,
        _ => null,
    };
}
