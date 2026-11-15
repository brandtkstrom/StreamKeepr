using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Emotes;
using StreamKeepr.Infrastructure.Twitch;

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
    private int _globalCalls;
    private int _channelCalls;

    public string Id { get; init; } = "bttv";

    public bool Enabled { get; init; } = true;

    public Func<CancellationToken, Task<IReadOnlyList<EmoteCatalogEntry>>> Global { get; set; } =
        _ => Task.FromResult<IReadOnlyList<EmoteCatalogEntry>>([]);

    public Func<string, CancellationToken, Task<IReadOnlyList<EmoteCatalogEntry>>> Channel
    {
        get;
        set;
    } = (_, _) => Task.FromResult<IReadOnlyList<EmoteCatalogEntry>>([]);

    public int GlobalCalls => Volatile.Read(ref _globalCalls);

    public int ChannelCalls => Volatile.Read(ref _channelCalls);

    public int Calls => GlobalCalls + ChannelCalls;

    public Task<IReadOnlyList<EmoteCatalogEntry>> GetGlobalAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _globalCalls);
        return Global(ct);
    }

    public Task<IReadOnlyList<EmoteCatalogEntry>> GetChannelAsync(
        string channelId, CancellationToken ct)
    {
        Interlocked.Increment(ref _channelCalls);
        return Channel(channelId, ct);
    }

    public EmoteImageSet GetStaticImage(EmoteCatalogEntry entry) =>
        new($"https://cdn.test/{entry.Id}/1x.webp",
            $"https://cdn.test/{entry.Id}/1x.webp 1x, " +
            $"https://cdn.test/{entry.Id}/2x.webp 2x, " +
            $"https://cdn.test/{entry.Id}/3x.webp 3x");
}

internal sealed class StubEmotePoller : IStreamPoller
{
    private int _lookups;

    public Func<string, CancellationToken, Task<ChannelProfile?>> Lookup { get; set; } =
        (_, _) => Task.FromResult<ChannelProfile?>(null);

    public int Lookups => Volatile.Read(ref _lookups);

    public Task<ChannelProfile?> GetUserAsync(string login, CancellationToken ct)
    {
        Interlocked.Increment(ref _lookups);
        return Lookup(login, ct);
    }

    public Task RunAsync(
        Func<IReadOnlyList<string>> logins,
        StreamObservationRoute route,
        CancellationToken ct) =>
        Task.Delay(Timeout.InfiniteTimeSpan, ct);

    public Task<ChannelInfo?> GetChannelInfoAsync(string broadcasterId, CancellationToken ct) =>
        Task.FromResult<ChannelInfo?>(null);

    public void Nudge()
    {
    }
}

internal static class EmoteTestData
{
    public static HttpResponseMessage Json(string json) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    public static HttpResponseMessage Json(
        System.Net.HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    public const string StaticId = "54fa8f1401e468494b85b537";
    public const string AnimatedId = "566ca38765dbbdab32ec0560";

    public static EmoteCatalogEntry Entry(
        string id = StaticId, string code = "KEKW", bool animated = false) =>
        new(id, code, animated);
}
