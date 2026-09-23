using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace StreamRecorder.Twitch;

public static class Extensions
{
    private static readonly HttpRetryStrategyOptions HttpRetryOptions = new()
    {
        ShouldHandle = args => ValueTask.FromResult(args.Outcome switch
        {
            {Result.StatusCode: HttpStatusCode.TooManyRequests} => false,
            {Result.StatusCode: HttpStatusCode.Unauthorized} => false,
            {Result.StatusCode: >= HttpStatusCode.InternalServerError} => true,
            {Exception: HttpRequestException or TimeoutRejectedException} => true,
            _ => false,
        }),
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true
    };

    public static IServiceCollection AddTwitchServices(this IServiceCollection services)
    {
        services.AddSingleton<IAuthTokenProvider, AuthTokenProvider>()
                .AddSingleton<ITwitchApiClient, TwitchApiClient>()
                .AddTransient<AuthenticationHandler>();

        services.RegisterHttpClient<IAuthTokenProvider, AuthTokenProvider>("https://id.twitch.tv");
        services.RegisterHttpClient<ITwitchApiClient, TwitchApiClient>("https://api.twitch.tv")
                .AddHttpMessageHandler<AuthenticationHandler>()
                .ConfigureHttpClient((sp, client) =>
                {
                    var config = sp.GetRequiredService<IOptions<AppConfig>>().Value;
                    client.DefaultRequestHeaders.Add("Client-ID", config.ClientId);
                });

        return services;
    }

    private static IHttpClientBuilder RegisterHttpClient<TClient, TImplementation>(this IServiceCollection services,
                                                                                   string baseAddress)
        where TClient : class
        where TImplementation : class, TClient
    {
        var httpClientBuilder = services.AddHttpClient<TClient, TImplementation>(config =>
        {
            config.BaseAddress = new Uri(baseAddress);
            config.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        httpClientBuilder.AddResilienceHandler(typeof(TImplementation).Name, config =>
        {
            config.AddRetry(HttpRetryOptions)
                  .AddTimeout(TimeSpan.FromSeconds(10));
        });

        return httpClientBuilder;
    }
}