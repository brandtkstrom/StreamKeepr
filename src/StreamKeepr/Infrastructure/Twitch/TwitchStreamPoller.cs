using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// Polls Helix <c>/streams</c> for every watched channel in batches of 100 and resolves
/// profiles through <c>/users</c>. Any batch failure becomes Unknown; it never finalizes a
/// recording.
/// </summary>
public sealed class TwitchStreamPoller(
    IHttpClientFactory httpFactory,
    TwitchTokenProvider tokens,
    PollSchedule schedule,
    DvrStatus status,
    IOptions<TwitchOptions> options,
    TimeProvider clock,
    ILogger<TwitchStreamPoller> logger) : IStreamPoller
{
    public async Task RunAsync(
        Func<IReadOnlyList<string>> logins,
        StreamObservationRoute route,
        CancellationToken ct)
    {
        try
        {
            TimeSpan normal = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
            TimeSpan withPush = TimeSpan.FromSeconds(
                options.Value.PollIntervalWithEventSubSeconds);

            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<string> channels = logins()
                    .Where(TwitchLogin.IsValid)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                bool succeeded = true;
                TimeSpan? roundTrip = null;
                if (channels.Count > 0)
                {
                    long started = clock.GetTimestamp();
                    IReadOnlyDictionary<string, StreamObservation> observed =
                        await ObserveAsync(channels, ct);

                    // Unknown only comes from a failed batch; the round trip is for the UI.
                    succeeded = observed.Values.All(
                        observation => observation is not StreamObservation.Unknown);
                    if (succeeded)
                        roundTrip = clock.GetElapsedTime(started);

                    foreach ((string login, StreamObservation observation) in observed)
                        await route(login, observation, ct);
                }

                bool recording = status.Current.Values.Any(
                    snapshot => snapshot.State is DvrState.Recording
                        or DvrState.WaitingToRetry);

                schedule.Polled(succeeded, roundTrip);
                await schedule.WaitAsync(
                    normal, recording ? normal : withPush, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    /// <summary>One /streams request per 100 logins; a batch failure is Unknown for that batch.</summary>
    public async Task<IReadOnlyDictionary<string, StreamObservation>> ObserveAsync(
        IReadOnlyList<string> logins, CancellationToken ct)
    {
        var result = new Dictionary<string, StreamObservation>(StringComparer.Ordinal);

        foreach (string[] batch in logins.Chunk(100))
        {
            Dictionary<string, LiveStream>? live;
            try
            {
                live = await LiveStreamsAsync(batch, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Helix /streams batch failed.");
                live = null;
            }

            foreach (string login in batch)
            {
                result[login] = live is null
                    ? new StreamObservation.Unknown("Helix request failed")
                    : live.TryGetValue(login, out LiveStream? stream)
                        ? new StreamObservation.Live(stream)
                        : new StreamObservation.Offline();
            }
        }

        return result;
    }

    public async Task<ChannelProfile?> GetUserAsync(string login, CancellationToken ct)
    {
        login = TwitchLogin.Normalize(login);
        HelixUsersResponse? payload = await GetHelixAsync(
            $"helix/users?login={Uri.EscapeDataString(login)}",
            TwitchJsonContext.Default.HelixUsersResponse,
            ct);

        HelixUser? user = payload?.Data?.FirstOrDefault();
        return user is null
            ? null
            : new ChannelProfile(
                user.Id, user.DisplayName, user.ProfileImageUrl);
    }

    /// <summary>Current title/game from Helix /channels, used after a push notification.</summary>
    public async Task<ChannelInfo?> GetChannelInfoAsync(
        string broadcasterId, CancellationToken ct)
    {
        HelixChannelsResponse? payload = await GetHelixAsync(
            $"helix/channels?broadcaster_id={Uri.EscapeDataString(broadcasterId)}",
            TwitchJsonContext.Default.HelixChannelsResponse,
            ct);

        HelixChannel? channel = payload?.Data?.FirstOrDefault();
        return channel is null
            ? null
            : new ChannelInfo(channel.Title, channel.GameName);
    }

    public void Nudge() => schedule.Nudge();

    private async Task<Dictionary<string, LiveStream>?> LiveStreamsAsync(
        IReadOnlyList<string> batch, CancellationToken ct)
    {
        string query = string.Join(
            '&',
            batch.Select(login => $"user_login={Uri.EscapeDataString(login)}"));

        HelixStreamsResponse? payload;
        try
        {
            payload = await GetHelixAsync(
                $"helix/streams?first=100&{query}",
                TwitchJsonContext.Default.HelixStreamsResponse,
                ct);
        }
        catch (TwitchApiException ex)
        {
            logger.LogWarning(ex, "Helix /streams batch failed.");
            return null;
        }

        if (payload is null)
            return null;

        return (payload.Data ?? [])
            .Where(stream => stream.Type == "live" && TwitchStreamId.IsNumeric(stream.Id))
            .ToDictionary(
                stream => stream.UserLogin,
                stream => new LiveStream(
                    stream.Id,
                    stream.Title,
                    stream.GameName,
                    stream.StartedAt,
                    stream.ThumbnailUrl),
                StringComparer.Ordinal);
    }

    private async Task<T?> GetHelixAsync<T>(
        string relativeUri,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
        where T : class
    {
        using HttpClient http = httpFactory.CreateClient(TwitchHttpClients.Helix);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                string token = await tokens.GetAsync(ct);

                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
                request.Headers.Add("Client-Id", options.Value.ClientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using HttpResponseMessage response = await http.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    tokens.Invalidate(token);
                    continue;
                }

                if (IsTransient(response.StatusCode) && attempt == 0)
                    continue;

                if (!response.IsSuccessStatusCode)
                {
                    throw new TwitchApiException(
                        $"Twitch returned HTTP {(int)response.StatusCode}.");
                }

                return await response.Content.ReadFromJsonAsync(typeInfo, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TwitchApiException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new TwitchApiException("Twitch request failed.", ex);
            }
        }

        return null;
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

}
