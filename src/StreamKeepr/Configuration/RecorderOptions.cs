using System.ComponentModel.DataAnnotations;

namespace StreamKeepr.Configuration;

public sealed class RecorderOptions
{
    public const string SectionName = "Recorder";

    [Required] public string StreamlinkPath { get; set; } = "streamlink";

    [Required] public string FfmpegPath { get; set; } = "ffmpeg";

    [Required] public string FfprobePath { get; set; } = "ffprobe";

    [Required] public string Quality { get; set; } = "best";

    // Optional: subscriber-only streams.
    public string? TwitchUserOAuthToken { get; set => field = value?.Trim(); }

    // The stitched playlist advertises twice this as its target duration (12 s by default);
    // the player's live-edge offset and 60 s forward buffer are measured against that cadence.
    [Range(2, 10)] public int SegmentSeconds { get; set; } = 6;

    [Range(1, 20)] public int HlsLiveEdge { get; set; } = 6;

    [Range(1, 20)] public int MaxRetries { get; set; } = 6;

    [Range(1, 60)] public int RetryBaseSeconds { get; set; } = 2;

    [Range(5, 600)] public int RetryMaxSeconds { get; set; } = 60;

    [Range(1, 20)] public int FailuresBeforeCooldown { get; set; } = 3;

    [Range(1, 1440)] public int CooldownMinutes { get; set; } = 15;

    [Range(15, 600)] public int StallTimeoutSeconds { get; set; } = 120;

    [Range(5, 15)] public int FinalizeTimeoutSeconds { get; set; } = 10;

    // A part shorter than this counts toward the failure streak instead of resetting it.
    [Range(5, 600)] public int HealthyPartSeconds { get; set; } = 30;
}
