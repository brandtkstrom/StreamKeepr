using System.Text;

namespace StreamKeepr.Ui;

/// <summary>
/// A channel's colour (glossary: <b>Channel colour</b>): one of the design system's
/// <c>channel-1</c> … <c>channel-8</c> tokens, chosen by a stable hash of the Twitch login.
/// It belongs to the login, so a removed channel's recordings keep it.
/// </summary>
/// <remarks>
/// The hash is 32-bit FNV-1a over the trimmed, lower-cased login's UTF-8 bytes. It must never
/// change: it is the only record of which colour a viewer has learned for a channel, and the
/// drawer, rail and chat all derive it independently.
/// </remarks>
public static class ChannelColor
{
    /// <summary>The number of channel colours in the design system.</summary>
    public const int Count = 8;

    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>The channel colour number, 1 to <see cref="Count"/>.</summary>
    public static int For(string login)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);

        uint hash = FnvOffsetBasis;
        foreach (byte value in Encoding.UTF8.GetBytes(login.Trim().ToLowerInvariant()))
        {
            hash ^= value;
            hash *= FnvPrime;
        }

        return (int)(hash % Count) + 1;
    }
}
