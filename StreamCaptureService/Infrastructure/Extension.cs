using dotenv.net;
using Serilog;

namespace StreamRecorder.Infrastructure;

public static class Extensions
{
    public static RecorderSettings LoadRecorderSettings(this HostApplicationBuilder builder)
    {
        // Local development convenience: pull .env into the process environment before the host
        // builds configuration, so the standard environment-variable provider sees the values.
        // Real environment variables and command-line arguments take precedence.
        if (File.Exists(".env"))
        {
            Log.Debug("Loading environment variables from .env file");
            DotEnv.Load(new DotEnvOptions());
        }

        var settings = builder.Configuration
                              .GetSection(RecorderSettings.SectionName)
                              .Get<RecorderSettings>()
                       ?? new RecorderSettings();

        return Configure(settings);
    }

    private static RecorderSettings Configure(RecorderSettings settings)
    {
        if (Environment.GetEnvironmentVariable("CLIENT_ID") is {Length: > 0} clientId)
            settings.ClientId = clientId;

        if (Environment.GetEnvironmentVariable("CLIENT_SECRET") is {Length: > 0} clientSecret)
            settings.ClientSecret = clientSecret;

        if (Environment.GetEnvironmentVariable("CHANNEL") is {Length: > 0} channel)
            settings.Channel = channel;

        if (Environment.GetEnvironmentVariable("USER_AUTH_TOKEN") is {Length: > 0} authToken)
            settings.UserAuthToken = authToken;

        if (int.TryParse(Environment.GetEnvironmentVariable("POLL_SECONDS"), out var pollSeconds) && pollSeconds >= 1)
            settings.PollSeconds = pollSeconds;

        if (Environment.GetEnvironmentVariable("ROOT_PATH") is {Length: > 0} path)
            settings.RootPath = path;

        if (Environment.GetEnvironmentVariable("LOG_LEVEL") is {Length: > 0} logLevel)
            settings.LogLevel = logLevel;

        return settings;
    }
}