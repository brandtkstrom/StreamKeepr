namespace StreamKeepr.Components.Primitives;

public enum ButtonVariant
{
    Filled,
    Tonal,
    Outlined,
    Text,
    Danger,
}

public enum IconSize
{
    Small = 18,
    Medium = 20,
    Large = 24,
}

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
}

public enum MenuAlignment
{
    Start,
    End,
}
