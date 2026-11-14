using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch.Chat;

namespace StreamKeepr.Mock;

/// <summary>Deterministic fake IRC source for the Mock profile and E2E; no network.</summary>
public sealed class MockChatSource(
    RecordingLibrary library,
    IOptions<StorageOptions> storage,
    IOptions<ChatOptions> options,
    TimeProvider clock,
    ILogger<MockChatSource> logger) : BackgroundService, IChatSource
{
    private readonly ChatEventQueue _events = new();

    private readonly ConcurrentDictionary<string, byte> _joined = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _baseAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _mediaEnd = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _joinedAt = new(StringComparer.Ordinal);
    private static readonly TimeSpan MessageInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Every 12th mock message is deleted, exercising query-time tombstones.</summary>
    private const int DeleteEvery = 12;

    private int _counter;

    public string Source => ChatLogStore.MockSource;

    public ValueTask JoinAsync(string channel, CancellationToken ct)
    {
        string login = TwitchLogin.Normalize(channel);
        if (!TwitchLogin.IsValid(login))
            return ValueTask.CompletedTask;

        this._joined[login] = 0;
        this._joinedAt[login] = clock.GetUtcNow().ToUnixTimeMilliseconds();
        this.RememberMedia(login);
        return ValueTask.CompletedTask;
    }

    public ValueTask PartAsync(string channel, CancellationToken ct)
    {
        string login = TwitchLogin.Normalize(channel);
        this._joined.TryRemove(login, out _);
        this._baseAt.TryRemove(login, out _);
        this._mediaEnd.TryRemove(login, out _);
        this._joinedAt.TryRemove(login, out _);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<ChatSourceEvent> ReadAllAsync(CancellationToken ct) =>
        this._events.ReadAllAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(MessageInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (string channel in this._joined.Keys)
                {
                    if (!this._baseAt.ContainsKey(channel))
                        this.RememberMedia(channel);

                    ChatMessage message = this.NextMessage(channel);
                    this._events.Write(new ChatSourceEvent.MessageEvent(message));

                    if (this._counter % DeleteEvery == 0)
                    {
                        this._events.Write(new ChatSourceEvent.Delete(
                            channel, message.AtMs, message.Id));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        finally
        {
            this._events.Complete();
        }
    }

    private void RememberMedia(string channel)
    {
        RecordingSummary? summary = library.All
            .Where(item => item.IsRecording &&
                           string.Equals(item.Channel, channel, StringComparison.Ordinal))
            .OrderByDescending(item => item.StartedAt)
            .FirstOrDefault();
        if (summary is null)
            return;

        string path = Path.Combine(
            storage.Value.RecordingsRoot, summary.Channel, summary.Id, PartFiles.Playlist(1));
        if (!File.Exists(path))
            return;

        try
        {
            string playlist = File.ReadAllText(path);
            DateTimeOffset? pdt = PlaylistStitcher.FirstProgramDateTime(playlist);
            if (pdt is null)
                return;

            this._baseAt[channel] = pdt.Value.AddSeconds(-options.Value.LeadInSeconds)
                .ToUnixTimeMilliseconds();
            this._mediaEnd[channel] = Math.Max(1, PlaylistStitcher.TotalDuration(playlist));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            logger.LogDebug(ex, "Mock chat could not read the media timeline for {Channel}.", channel);
        }
    }

    private ChatMessage NextMessage(string channel)
    {
        int counter = Interlocked.Increment(ref this._counter);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        double mediaEnd = this._mediaEnd.GetValueOrDefault(channel, 60);
        long joinedAt = this._joinedAt.GetValueOrDefault(channel, now);
        double elapsed = Math.Max(0, (now - joinedAt) / 1000.0);
        double media = Math.Min(Math.Max(1, mediaEnd - 1),
            Math.Max(1, mediaEnd - 10) + elapsed);
        long at = this._baseAt.TryGetValue(channel, out long baseAt)
            ? baseAt + (long)(media * 1000)
            : now;
        string text = (counter % 4) switch
        {
            0 => "hello Kappa",
            1 => "nice stream PogChamp",
            2 => "this is a mock chat message",
            _ => "raid incoming!",
        };

        return new ChatMessage(
            $"mock-{channel}-{counter}",
            channel,
            at,
            AtIsLocal: false,
            $"mock-user-{counter % 5}",
            $"viewer{counter % 5}",
            $"Viewer {counter % 5}",
            counter % 2 == 0 ? "#1F6FEB" : null,
            counter % 3 == 0
                ? [new ChatBadge("moderator", "1", null)]
                : [],
            text.Contains("Kappa", StringComparison.Ordinal)
                ? [new ChatEmote("25", [new ChatEmoteRange(6, 10)])]
                : [],
            null,
            null,
            Action: false,
            text);
    }

    public ValueTask DisposeAsync()
    {
        this._events.Complete();
        return ValueTask.CompletedTask;
    }
}
