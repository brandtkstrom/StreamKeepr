using System.Net;
using System.Net.Http.Json;
using AspNet.Security.OAuth.Twitch;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Twitch;

internal sealed class RecordingAdminTokenStore : IAdminTokenStore
{
    public event Action? Changed;

    public AdminToken? Token { get; private set; }

    public List<AdminToken> SavedTokens { get; } = [];

    public Exception? SaveFailure { get; set; }

    public int ClearCount { get; private set; }

    public int RefreshCount { get; private set; }

    public ValueTask<string?> GetAsync(CancellationToken ct) =>
        ValueTask.FromResult(Token?.AccessToken);

    public ValueTask<string?> RefreshAsync(CancellationToken ct)
    {
        RefreshCount++;
        return ValueTask.FromResult(Token?.AccessToken);
    }

    public Task SaveAsync(AdminToken token, CancellationToken ct)
    {
        if (SaveFailure is not null)
            throw SaveFailure;

        SavedTokens.Add(token);
        Token = token;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct)
    {
        ClearCount++;
        Token = null;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task WaitForTokenAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Answers the OAuth token exchange and the Twitch user-info lookup. Any other
/// request fails the test so nothing can reach the real Twitch endpoints.
/// </summary>
internal sealed class TwitchOAuthStub : HttpMessageHandler
{
    public const string AccessToken = "twitch-access-token";
    public const string RefreshToken = "twitch-refresh-token";

    public string Login { get; init; } = "allowed_login";

    public bool IncludeLogin { get; init; } = true;

    public int ExpiresInSeconds { get; init; } = 3600;

    public bool FailTokenExchange { get; init; }

    public List<Uri> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);

        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/oauth2/token")
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = QueryHelpers.ParseQuery($"?{body}");
            Assert.Equal("authorization_code", form["grant_type"].ToString());
            Assert.Equal("test-client-id", form["client_id"].ToString());
            Assert.Equal("test-code", form["code"].ToString());
            Assert.EndsWith(AdminAuth.CallbackPath, form["redirect_uri"].ToString(), StringComparison.Ordinal);

            if (FailTokenExchange)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = JsonContent.Create(new { error = "invalid_grant" }),
                };

            return Json(new
            {
                access_token = AccessToken,
                refresh_token = RefreshToken,
                token_type = "bearer",
                expires_in = ExpiresInSeconds,
            });
        }

        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/helix/users/")
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(AccessToken, request.Headers.Authorization?.Parameter);
            Assert.Equal("test-client-id", request.Headers.GetValues("Client-ID").Single());

            var user = new Dictionary<string, object?>
            {
                ["id"] = "123456",
                ["display_name"] = "Test Streamer",
                ["profile_image_url"] = "https://example.invalid/avatar.png",
            };
            if (IncludeLogin)
                user["login"] = Login;

            return Json(new { data = new object[] { user } });
        }

        throw new InvalidOperationException(
            $"Unexpected Twitch request: {request.Method} {request.RequestUri}");
    }

    private static HttpResponseMessage Json(object payload) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
}

internal sealed record SignInResponse(
    HttpStatusCode ChallengeStatus,
    string AuthorizeUrl,
    HttpStatusCode CallbackStatus,
    string? CallbackLocation,
    IReadOnlyList<string> SetCookies,
    string? AuthCookie)
{
    public string? AuthCookieValue => AuthCookie is null
        ? null
        : AuthCookie[(AdminAuth.CookieScheme.Length + 1)..].Split(';')[0];
}

/// <summary>
/// Runs the real OAuth handler against <see cref="TwitchOAuthStub"/> with a manual
/// clock and an in-memory token store. HTTPS is the default because the framework's
/// OAuth correlation cookie is Secure and a cookie container drops it over HTTP.
/// </summary>
internal sealed class AdminAuthHarness : IDisposable
{
    // Far enough ahead that the framework's short-lived OAuth correlation cookie
    // (minted from this clock) is not already expired in wall-clock time.
    public static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _twitchBackchannel;
    private readonly HttpClient _client;

    public AdminAuthHarness(
        string login = "allowed_login",
        IReadOnlyDictionary<string, string?>? overrides = null,
        bool includeLogin = true,
        string baseAddress = "https://localhost",
        IPAddress? remoteIpAddress = null,
        bool failTokenExchange = false)
    {
        Stub = new TwitchOAuthStub
        {
            Login = login,
            IncludeLogin = includeLogin,
            FailTokenExchange = failTokenExchange,
        };
        Store = new RecordingAdminTokenStore();
        Clock = new ManualTimeProvider(Start);
        _twitchBackchannel = new HttpClient(Stub, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        Factory = new StreamKeeprAppFactory(overrides, configureTestServices: services =>
        {
            services.PostConfigure<TwitchAuthenticationOptions>(
                TwitchAuthenticationDefaults.AuthenticationScheme,
                options => options.Backchannel = _twitchBackchannel);
            services.PostConfigure<CookieAuthenticationOptions>(
                AdminAuth.CookieScheme,
                options => options.TimeProvider = Clock);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IAdminTokenStore>();
            services.AddSingleton<IAdminTokenStore>(Store);

            if (remoteIpAddress is not null)
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(remoteIpAddress));
        });

        _client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(baseAddress),
        });
    }

    public StreamKeeprAppFactory Factory { get; }

    public TwitchOAuthStub Stub { get; }

    public RecordingAdminTokenStore Store { get; }

    public ManualTimeProvider Clock { get; }

    public HttpClient Client => _client;

    public async Task<SignInResponse> SignInAsync(string? returnUrl = null)
    {
        var (challengeStatus, authorizeUrl, state) = await StartChallengeAsync(returnUrl);

        using var callback = await _client.GetAsync(
            $"{AdminAuth.CallbackPath}?code=test-code&state={Uri.EscapeDataString(state)}");
        return BuildResponse(challengeStatus, authorizeUrl, callback);
    }

    public async Task<SignInResponse> AccessDeniedAsync(string? returnUrl = null)
    {
        var (challengeStatus, authorizeUrl, state) = await StartChallengeAsync(returnUrl);

        using var callback = await _client.GetAsync(
            $"{AdminAuth.CallbackPath}?error=access_denied&state={Uri.EscapeDataString(state)}");
        return BuildResponse(challengeStatus, authorizeUrl, callback);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => _client.GetAsync(path);

    /// <summary>
    /// Creates an antiforgery token bound to the signed-in principal and returns
    /// the cookie and form field needed by a real logout form post.
    /// </summary>
    public AntiforgeryRequestToken GetAntiforgeryToken(string authCookieValue)
    {
        var ticket = GetCookieOptions(this).TicketDataFormat.Unprotect(authCookieValue)
            ?? throw new InvalidOperationException("The admin cookie could not be decrypted.");

        using var scope = Factory.Services.CreateScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = ticket.Principal,
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");

        var tokens = scope.ServiceProvider.GetRequiredService<IAntiforgery>()
            .GetAndStoreTokens(context);
        var setCookie = context.Response.Headers.SetCookie.ToString();
        var cookie = setCookie.Split(';', 2)[0];

        return new AntiforgeryRequestToken(tokens.FormFieldName, tokens.RequestToken!, cookie);
    }

    public HttpClient CreateRequestClient(string baseAddress = "https://localhost") =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri(baseAddress),
        });

    public void Dispose()
    {
        _client.Dispose();
        _twitchBackchannel.Dispose();
        Factory.Dispose();
    }

    private async Task<(HttpStatusCode Status, string AuthorizeUrl, string State)> StartChallengeAsync(
        string? returnUrl)
    {
        var loginPath = returnUrl is null
            ? AdminAuth.LoginPath
            : $"{AdminAuth.LoginPath}?returnUrl={Uri.EscapeDataString(returnUrl)}";

        using var challenge = await _client.GetAsync(loginPath);
        var authorizeUrl = challenge.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("The login endpoint did not challenge.");
        var state = QueryHelpers.ParseQuery(new Uri(authorizeUrl).Query)["state"].ToString();

        return (challenge.StatusCode, authorizeUrl, state);
    }

    private static SignInResponse BuildResponse(
        HttpStatusCode challengeStatus, string authorizeUrl, HttpResponseMessage callback)
    {
        var setCookies = callback.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : [];
        var authCookie = setCookies.FirstOrDefault(
            cookie => cookie.StartsWith($"{AdminAuth.CookieScheme}=", StringComparison.Ordinal));

        return new SignInResponse(
            challengeStatus,
            authorizeUrl,
            callback.StatusCode,
            callback.Headers.Location?.ToString(),
            setCookies,
            authCookie);
    }

    private static CookieAuthenticationOptions GetCookieOptions(AdminAuthHarness harness) =>
        harness.Factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AdminAuth.CookieScheme);
}

internal sealed record AntiforgeryRequestToken(
    string FormFieldName, string RequestToken, string Cookie);

internal static class AdminAuthTestSettings
{
    public static Dictionary<string, string?> Allowlist(params string[] logins)
    {
        var settings = new Dictionary<string, string?>();
        for (var index = 0; index < logins.Length; index++)
            settings[$"{AuthOptions.SectionName}:AllowedTwitchLogins:{index}"] = logins[index];
        return settings;
    }

    public static Dictionary<string, string?> TrustedProxies(params string[] proxies)
    {
        var settings = new Dictionary<string, string?>();
        for (var index = 0; index < proxies.Length; index++)
        {
            settings[
                $"{ForwardedHeadersSettings.SectionName}:{nameof(ForwardedHeadersSettings.KnownProxies)}:{index}"] =
                proxies[index];
        }

        return settings;
    }

    public static Dictionary<string, string?> TrustedNetworks(params string[] networks)
    {
        var settings = new Dictionary<string, string?>();
        for (var index = 0; index < networks.Length; index++)
        {
            settings[
                $"{ForwardedHeadersSettings.SectionName}:{nameof(ForwardedHeadersSettings.KnownNetworks)}:{index}"] =
                networks[index];
        }

        return settings;
    }
}

/// <summary>
/// Makes the test connection look like it came from a specific address so the
/// forwarded-header trust rules can be exercised without changing the app.
/// </summary>
internal sealed class RemoteIpStartupFilter(IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
}
