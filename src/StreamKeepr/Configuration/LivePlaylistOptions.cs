using System.ComponentModel.DataAnnotations;

namespace StreamKeepr.Configuration;

public sealed class LivePlaylistOptions
{
    public const string SectionName = "LivePlaylist";

    /// <summary>How often the background refresher rebuilds each live snapshot.</summary>
    [Range(1, 3600)] public int RefreshSeconds { get; set; } = 1;

    /// <summary>Bounds one session's refresh; a timeout keeps the last-known-good body.</summary>
    [Range(1, 300)] public int RefreshTimeoutSeconds { get; set; } = 5;
}
