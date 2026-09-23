using Microsoft.Extensions.Options;

namespace StreamRecorder.Twitch;

public interface IAuthTokenProvider
{
    ValueTask<string> GetTokenAsync(bool forceRefresh = false, CancellationToken cancelToken = default);
}

public class AuthTokenProvider : IAuthTokenProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly HttpClient _httpClient;
    private readonly AppConfig _appConfig;
    private readonly ILogger<AuthTokenProvider> _logger;

    private string? _token = string.Empty;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public AuthTokenProvider(HttpClient httpClient,
                             IOptions<AppConfig> appConfig,
                             ILogger<AuthTokenProvider> logger)
    {
        _httpClient = httpClient;
        _appConfig = appConfig.Value;
        _logger = logger;
    }

    public async ValueTask<string> GetTokenAsync(bool forceRefresh = false, CancellationToken cancelToken = default)
    {
        if (!forceRefresh && ValidTokenSet())
            return _token!;

        await _gate.WaitAsync(TimeSpan.FromSeconds(30), cancelToken);

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _appConfig.ClientId,
                ["client_secret"] = _appConfig.ClientSecret
            });

            var response = await _httpClient.PostAsync("/oauth2/token", TwitchJsonContext.Default.GetTokenDto, content, cancelToken);

            if (!string.IsNullOrWhiteSpace(response.Error))
                _logger.LogWarning(response.Error);
            if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Data.AccessToken))
                throw new ArgumentNullException(nameof(GetTokenDto.AccessToken), "Access token is missing");

            _token = response.Data.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(response.Data.ExpiresIn);

            return _token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting access token");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///  Token set and more than 5 minutes until expiration
    /// </summary>
    private bool ValidTokenSet()
        => !string.IsNullOrEmpty(_token) && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(5);
}

public sealed record GetTokenDto
{
    public string? AccessToken { get; init; }

    public int ExpiresIn { get; init; }

    public string? TokenType { get; init; }
}