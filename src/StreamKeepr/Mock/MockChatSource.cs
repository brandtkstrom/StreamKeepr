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
    private readonly ConcurrentDictionary<string, double> _partEnd = new(StringComparer.Ordinal);
    private static readonly TimeSpan MessageInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Every 12th mock message is deleted, exercising query-time tombstones.</summary>
    private const int DeleteEvery = 12;

    /// <summary>Messages land just behind the recorded edge, where a live viewer can reach them.</summary>
    private const double EdgeLagSeconds = 2;

    private int _counter;

    public string Source => ChatLogStore.MockSource;

    public ValueTask JoinAsync(string channel, CancellationToken ct)
    {
        string login = TwitchLogin.Normalize(channel);
        if (!TwitchLogin.IsValid(login))
            return ValueTask.CompletedTask;

        _joined[login] = 0;
        RememberMedia(login);
        return ValueTask.CompletedTask;
    }

    public ValueTask PartAsync(string channel, CancellationToken ct)
    {
        string login = TwitchLogin.Normalize(channel);
        _joined.TryRemove(login, out _);
        _baseAt.TryRemove(login, out _);
        _partEnd.TryRemove(login, out _);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<ChatSourceEvent> ReadAllAsync(CancellationToken ct) =>
        _events.ReadAllAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(MessageInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (string channel in _joined.Keys)
                {
                    // A live capture keeps growing, so follow its edge every tick.
                    RememberMedia(channel);

                    ChatMessage message = NextMessage(channel);
                    _events.Write(new ChatSourceEvent.MessageEvent(message));

                    if (_counter % DeleteEvery == 0)
                    {
                        _events.Write(new ChatSourceEvent.Delete(
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
            _events.Complete();
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

        string directory = Path.Combine(storage.Value.RecordingsRoot, summary.Channel, summary.Id);
        if (!Directory.Exists(directory))
            return;

        try
        {
            // The newest part holds the live edge; a reconnect starts a new part.
            int newest = Directory.EnumerateFiles(directory, "p*.m3u8")
                .Select(path => PartFiles.TryParsePlaylist(Path.GetFileName(path), out int part)
                    ? part
                    : 0)
                .DefaultIfEmpty(0)
                .Max();
            if (newest < 1)
                return;

            string playlist = File.ReadAllText(Path.Combine(directory, PartFiles.Playlist(newest)));
            DateTimeOffset? pdt = PlaylistStitcher.FirstProgramDateTime(playlist);
            if (pdt is null)
                return;

            _baseAt[channel] = pdt.Value.AddSeconds(-options.Value.LeadInSeconds)
                .ToUnixTimeMilliseconds();
            _partEnd[channel] = Math.Max(1, PlaylistStitcher.TotalDuration(playlist));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            logger.LogDebug(ex, "Mock chat could not read the media timeline for {Channel}.", channel);
        }
    }

    private ChatMessage NextMessage(string channel)
    {
        int counter = Interlocked.Increment(ref _counter);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        // Seconds into the newest part, just behind its recorded end.
        double offset = Math.Max(1, _partEnd.GetValueOrDefault(channel, 60) - EdgeLagSeconds);
        long at = _baseAt.TryGetValue(channel, out long baseAt)
            ? baseAt + (long)(offset * 1000)
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
        _events.Complete();
        return ValueTask.CompletedTask;
    }
}
