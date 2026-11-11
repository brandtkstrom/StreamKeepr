using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using StreamRecorder.Config;

namespace StreamRecorder.Configuration;

public sealed class AppConfigValidator : IValidateOptions<AppConfig>
{
    public ValidateOptionsResult Validate(string? name, AppConfig options)
    {
        var twitchContext = new ValidationContext(options.TwitchOptions);
        var recorderContext = new ValidationContext(options.RecorderOptions);

        var results = new List<ValidationResult>();

        var ok = Validator.TryValidateObject(options, twitchContext, results, validateAllProperties: true)
                 && Validator.TryValidateObject(options, recorderContext, results, validateAllProperties: true);

        return ok
                   ? ValidateOptionsResult.Success
                   : ValidateOptionsResult.Fail(results.Select(r => r.ErrorMessage ?? "config error"));
    }
}
