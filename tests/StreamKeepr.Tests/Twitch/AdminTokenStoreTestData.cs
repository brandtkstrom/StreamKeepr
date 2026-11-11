using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;
using StreamKeepr.Infrastructure.Twitch;

namespace StreamKeepr.Tests.Twitch;

internal sealed class TempRecordingsRoot : IDisposable
{
    public TempRecordingsRoot() =>
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "streamkeepr-tests",
            Guid.NewGuid().ToString("N"));

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class TestDataProtection : IDisposable
{
    private readonly ServiceProvider _services;

    public TestDataProtection(string keyDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
            .SetApplicationName(DataProtectionKeyRing.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        _services = services.BuildServiceProvider();
    }

    public IDataProtectionProvider Provider => _services.GetRequiredService<IDataProtectionProvider>();

    public void Dispose() => _services.Dispose();
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("https://id.twitch.tv/") };
}

internal sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, string? Body);

internal sealed class RecordingHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri, body));
        return respond(request);
    }
}

internal static class AdminTokenStoreTestData
{
    public static AdminTokenStore CreateStore(
        string recordingsRoot,
        IDataProtectionProvider dataProtection,
        HttpMessageHandler handler,
        TimeProvider clock,
        ILogger<AdminTokenStore> logger) =>
        new(
            dataProtection,
            new StubHttpClientFactory(handler),
            Options.Create(new StorageOptions { RecordingsRoot = recordingsRoot }),
            Options.Create(new TwitchOptions
            {
                ClientId = "test-client-id",
                ClientSecret = "test-client-secret",
            }),
            clock,
            logger);

    public static HttpResponseMessage TokenResponse(
        string accessToken, string refreshToken, int expiresIn = 3600) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                access_token = accessToken,
                refresh_token = refreshToken,
                expires_in = expiresIn,
            }),
        };

    public static AdminToken Token(
        DateTimeOffset expiresAt,
        string accessToken = "access-1",
        string refreshToken = "refresh-1") =>
        new("streamer", accessToken, refreshToken, expiresAt);
}
