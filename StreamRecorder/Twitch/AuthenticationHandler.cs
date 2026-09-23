using System.Net;
using System.Net.Http.Headers;

namespace StreamRecorder.Twitch;

public class AuthenticationHandler : DelegatingHandler
{
    private readonly IAuthTokenProvider _authTokenProvider;
    private readonly ILogger<AuthenticationHandler> _logger;

    public AuthenticationHandler(IAuthTokenProvider authTokenProvider, ILogger<AuthenticationHandler> logger)
    {
        _authTokenProvider = authTokenProvider;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancelToken)
    {
        // Set request message auth header
        if (request.Headers.Authorization is not {Scheme: "Bearer", Parameter: not null})
        {
            _logger.LogDebug("Setting http request Authorization header");
            try
            {
                var authToken = await _authTokenProvider.GetTokenAsync(cancelToken: cancelToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting api client auth header");
                throw;
            }
        }

        var response = await base.SendAsync(request, cancelToken);

        // token may have been revoked server-side; refresh and retry
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogDebug("Unauthorized API response - refresh auth token and retry");

            response.Dispose();
            var updatedToken = await _authTokenProvider.GetTokenAsync(forceRefresh: true, cancelToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", updatedToken);

            response = await base.SendAsync(request, cancelToken);
        }

        return response;
    }
}