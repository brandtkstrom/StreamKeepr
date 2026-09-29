namespace StreamRecorder.Infrastructure;

public record RecorderSettings
{
    public const string SectionName = "RecorderSettings";

    /// <summary>Twitch application client ID; required.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Twitch application client secret; required.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Twitch login to record; required.</summary>
    public string Channel { get; set; } = "";

    /// <summary>Optional Twitch account OAuth token used to authenticate Streamlink's API requests.</summary>
    public string UserAuthToken { get; set; } = "";

    /// <summary>Liveness poll interval in seconds; must be at least 1.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Base directory for recordings and logs.</summary>
    public string RootPath { get; set; } = "/recordings";

    /// <summary>Serilog minimum level name; see <see cref="Logging.TryParseLevel"/>.</summary>
    public string LogLevel { get; set; } = "Information";
}