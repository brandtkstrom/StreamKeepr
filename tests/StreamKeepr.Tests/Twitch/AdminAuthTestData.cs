using System.Net;
using System.Net.Http.Json;
using AspNet.Security.OAuth.Twitch;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Twitch;

internal sealed class RecordingAdminTokenStore : IAdminTokenStore
{
    public event Action? Changed;

    public AdminToken? Token { get; private set; }

    public List<AdminToken> SavedTokens { get; } = [];

    public ValueTask<string?> GetAsync(CancellationToken ct) =>
        ValueTask.FromResult(Token?.AccessToken);

    public Task SaveAsync(AdminToken token, CancellationToken ct)
    {
        SavedTokens.Add(token);
        Token = token;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct)
    {
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
        bool includeLogin = true)
    {
        Stub = new TwitchOAuthStub { Login = login, IncludeLogin = includeLogin };
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
        });

        _client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
    }

    public StreamKeeprAppFactory Factory { get; }

    public TwitchOAuthStub Stub { get; }

    public RecordingAdminTokenStore Store { get; }

    public ManualTimeProvider Clock { get; }

    public async Task<SignInResponse> SignInAsync(string? returnUrl = null)
    {
        var loginPath = returnUrl is null
            ? AdminAuth.LoginPath
            : $"{AdminAuth.LoginPath}?returnUrl={Uri.EscapeDataString(returnUrl)}";

        using var challenge = await _client.GetAsync(loginPath);
        var authorizeUrl = challenge.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("The login endpoint did not challenge.");
        var state = QueryHelpers.ParseQuery(new Uri(authorizeUrl).Query)["state"].ToString();

        using var callback = await _client.GetAsync(
            $"{AdminAuth.CallbackPath}?code=test-code&state={Uri.EscapeDataString(state)}");
        var setCookies = callback.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : [];
        var authCookie = setCookies.FirstOrDefault(
            cookie => cookie.StartsWith($"{AdminAuth.CookieScheme}=", StringComparison.Ordinal));

        return new SignInResponse(
            challenge.StatusCode,
            authorizeUrl,
            callback.StatusCode,
            callback.Headers.Location?.ToString(),
            setCookies,
            authCookie);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => _client.GetAsync(path);

    public void Dispose()
    {
        _client.Dispose();
        _twitchBackchannel.Dispose();
        Factory.Dispose();
    }
}
