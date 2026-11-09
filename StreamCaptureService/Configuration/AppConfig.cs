using System.ComponentModel.DataAnnotations;

namespace StreamRecorder.Config;

public sealed record AppConfig(TwitchOptions TwitchOptions, RecorderOptions RecorderOptions);

public sealed class TwitchOptions
{
    public const string SectionName = "Twitch";

    [Required]
    public string ClientId { get; set; } = "";

    [Required]
    public string ClientSecret { get; set; } = "";

    [Required, RegularExpression("^[A-Za-z0-9_]{1,25}$")]
    public string UserLogin { get; set; } = "";

    public string UserAuthToken { get; set; } = "";
}

public sealed class RecorderOptions
{
    public const string SectionName = "Recorder";

    [Required]
    public string OutputDirectory { get; set; } = "/recordings";

    [Required]
    public string Quality { get; set; } = "best";

    [Required]
    public string StreamlinkPath { get; set; } = "streamlink";

    [Required]
    public string FfmpegPath { get; set; } = "ffmpeg";

    // Target HLS segment length; FFmpeg cuts at the next keyframe after it when copying
    [Range(2, 30)]
    public int HlsSegmentSeconds { get; set; } = 6;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan ForcefulKillDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan StallThreshold { get; set; } = TimeSpan.FromMinutes(2);
}