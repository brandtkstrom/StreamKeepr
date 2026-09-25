using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace StreamRecorder;

public record AppConfig
{
    [ConfigurationKeyName("CLIENT_ID")]
    [Required(ErrorMessage = "Twitch client id is required"),
     RegularExpression(@"^\w{10,100}$", ErrorMessage = "{0} is invalid")]
    public string ClientId { get; init; } = string.Empty;

    [ConfigurationKeyName("CLIENT_SECRET")]
    [Required(ErrorMessage = "Twitch client secret is required"),
     RegularExpression(@"^\w{10,100}$", ErrorMessage = "{0} is invalid")]
    public string ClientSecret { get; init; } = string.Empty;

    [ConfigurationKeyName("USER_AUTH_TOKEN")]
    [RegularExpression(@"^\w{0,100}$", ErrorMessage = "{0} is invalid")]
    public string UserAuthToken { get; init; } = string.Empty;

    [ConfigurationKeyName("CHECK_INTERVAL_SECONDS")]
    [Range(1, 60 * 60 * 24, ErrorMessage = "{0} must be between {1} and {2}")]
    public uint CheckIntervalSeconds { get; init; } = 60;

    [ConfigurationKeyName("CHANNEL_NAME")]
    [Required(ErrorMessage = "Twitch channel name is required")]
    public string ChannelName { get; init; } = string.Empty;

    [ConfigurationKeyName("OUTPUT_DIR")]
    public string OutputDir { get; init; } = "recordings";

    [ConfigurationKeyName("STREAMLINK_CMD_ARGS")]
    public string[] StreamLinkArgs { get; init; } = Array.Empty<string>();
}

public sealed class AppConfigValidator : IValidateOptions<AppConfig>
{
    public ValidateOptionsResult Validate(string? name, AppConfig options)
    {
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        return ok
                   ? ValidateOptionsResult.Success
                   : ValidateOptionsResult.Fail(results.Select(r => r.ErrorMessage ?? "config error"));
    }
}