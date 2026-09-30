using Microsoft.Extensions.Options;

namespace StreamRecorder.Infrastructure;

public static class Extensions
{
    public static HostApplicationBuilder LoadAppConfig(this HostApplicationBuilder builder)
    {
        // Load & validate config
        builder.Services
               .AddSingleton<IValidateOptions<AppConfig>, AppConfigValidator>()
               .AddOptions<AppConfig>()
               .Bind(builder.Configuration.GetSection(AppConfig.SectionName))
               .Configure(ApplyEnvironmentVariables)
               .ValidateDataAnnotations()
               .ValidateOnStart();

        return builder;
    }

    private static void ApplyEnvironmentVariables(AppConfig config)
    {
        if (Environment.GetEnvironmentVariable("CLIENT_ID") is {Length: > 0} clientId)
            config.ClientId = clientId;

        if (Environment.GetEnvironmentVariable("CLIENT_SECRET") is {Length: > 0} clientSecret)
            config.ClientSecret = clientSecret;

        if (Environment.GetEnvironmentVariable("CHANNEL_NAME") is {Length: > 0} channel)
            config.ChannelName = channel;

        if (Environment.GetEnvironmentVariable("USER_AUTH_TOKEN") is {Length: > 0} authToken)
            config.UserAuthToken = authToken;

        if (int.TryParse(Environment.GetEnvironmentVariable("CHECK_INTERVAL_SECONDS"), out var pollSeconds) && pollSeconds >= 1)
            config.CheckIntervalSeconds = pollSeconds;

        if (Environment.GetEnvironmentVariable("OUTPUT_PATH") is {Length: > 0} path)
            config.OutputPath = path;

        if (Environment.GetEnvironmentVariable("LOG_LEVEL") is {Length: > 0} logLevel)
            config.LogLevel = logLevel;
    }
}