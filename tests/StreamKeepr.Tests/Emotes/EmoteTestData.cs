using StreamKeepr.Infrastructure.Emotes;

namespace StreamKeepr.Tests.Emotes;

internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri? RequestUri,
    IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>
/// A deterministic HTTP surface for provider tests. Every request is captured before the
/// response factory runs so assertions never observe a disposed message.
/// </summary>
internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    private readonly List<CapturedRequest> _requests = [];

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_requests)
                return _requests.ToArray();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            foreach (string value in header.Value)
                headers.Add(new(header.Key, value));
        }

        lock (_requests)
            _requests.Add(new(request.Method, request.RequestUri, headers));

        return await respond(request, cancellationToken);
    }
}

internal sealed class EmoteHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(EmoteHttpClients.BttvBaseAddress),
        };
}

internal sealed class StubEmoteProvider : IEmoteProvider
{
    private int _calls;

    public string Id { get; init; } = "bttv";

    public Func<EmoteChannel, CancellationToken, Task<EmoteProviderCatalog>> Fetch { get; set; } =
        (_, _) => Task.FromResult(new EmoteProviderCatalog([], []));

    public int Calls => Volatile.Read(ref _calls);

    public Task<EmoteProviderCatalog> GetCatalogAsync(EmoteChannel channel, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Fetch(channel, ct);
    }

    public EmoteImageSet GetStaticImage(EmoteCatalogEntry entry) =>
        new($"https://cdn.test/{entry.Id}/1x.webp",
            $"https://cdn.test/{entry.Id}/1x.webp 1x, " +
            $"https://cdn.test/{entry.Id}/2x.webp 2x, " +
            $"https://cdn.test/{entry.Id}/3x.webp 3x");
}

internal static class EmoteTestData
{
    public static HttpResponseMessage Json(string json) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    public const string StaticId = "54fa8f1401e468494b85b537";
    public const string AnimatedId = "566ca38765dbbdab32ec0560";

    public static EmoteCatalogEntry Entry(
        string id = StaticId, string code = "KEKW", bool animated = false, bool modifier = false) =>
        new(id, code, animated, modifier);
}
