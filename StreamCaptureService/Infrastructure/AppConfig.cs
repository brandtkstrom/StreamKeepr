using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

 namespace StreamRecorder.Infrastructure;

public record AppConfig
{
    public const string SectionName = "AppConfig";

    [ConfigurationKeyName("CLIENT_ID")]
    [Required(ErrorMessage = "Twitch client id is required"),
     RegularExpression(@"^\w{10,100}$", ErrorMessage = "{0} is invalid")]
    public string ClientId { get; set; } = string.Empty;

    [ConfigurationKeyName("CLIENT_SECRET")]
    [Required(ErrorMessage = "Twitch client secret is required"),
     RegularExpression(@"^\w{10,100}$", ErrorMessage = "{0} is invalid")]
    public string ClientSecret { get; set; } = string.Empty;

    [ConfigurationKeyName("USER_AUTH_TOKEN")]
    [RegularExpression(@"^\w{0,100}$", ErrorMessage = "{0} is invalid")]
    public string UserAuthToken { get; set; } = string.Empty;

    [ConfigurationKeyName("CHECK_INTERVAL_SECONDS")]
    [Range(1, 60 * 60 * 24, ErrorMessage = "{0} must be between {1} and {2}")]
    public int CheckIntervalSeconds { get; set; } = 60;

    [ConfigurationKeyName("CHANNEL_NAME")]
    [Required(ErrorMessage = "Twitch channel name is required")]
    public string ChannelName { get; set; } = string.Empty;

    [ConfigurationKeyName("OUTPUT_PATH")]
    public string OutputPath { get; set; } = "recordings";

    [ConfigurationKeyName("LOG_LEVEL")]
    public string LogLevel { get; set; } = "information";
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