using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Mock;

/// <summary>
/// Seeds deterministic chat logs for the finished Mock recordings through the chat store, so
/// they land in the record store like captured chat. It runs after <see cref="MockSeeder"/> has
/// recorded the sessions; a recording that already has chat is left alone.
/// </summary>
public sealed class MockChatSeeder(
    RecordingLibrary library,
    ChatLogStore store,
    IOptions<ChatOptions> options,
    ILogger<MockChatSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (RecordingSummary summary in library.All.Where(summary => !summary.IsRecording))
        {
            string directory;
            try
            {
                directory = store.SessionDirectory(summary);
            }
            catch (ArgumentException)
            {
                continue;
            }

            string playlist = Path.Combine(directory, PartFiles.Playlist(1));
            if (!File.Exists(playlist) || await store.HasChatAsync(summary.Id, cancellationToken))
                continue;

            DateTimeOffset? pdt = PlaylistTimeline.TryParse(File.ReadAllText(playlist))
                ?.FirstProgramDateTime;
            if (pdt is null)
                continue;

            store.OpenSession(summary, ChatSources.Mock);
            long baseAt = pdt.Value.AddSeconds(-options.Value.LeadInSeconds)
                .ToUnixTimeMilliseconds();
            Append(store, summary.Channel, baseAt, 5, "seed-1", "Welcome to the Mock stream",
                "moderator", "1");
            Append(store, summary.Channel, baseAt, 2, "seed-emotes",
                "Fixture emotes KEKW :tf: CatJAM BrokenEmote", null, null);
            // The streamer's own message, so the chat shows the channel colour (#180).
            Append(store, summary.Channel, baseAt, 9, "seed-streamer", "Thanks for watching the replay",
                "broadcaster", "1", Chatter.Streamer(summary.Channel));
            Append(store, summary.Channel, baseAt, 16, "seed-2", "Kappa this is a chat replay",
                null, null);
            Append(store, summary.Channel, baseAt, 27, "seed-3", "PogChamp nice clip",
                null, null);
            // Only the channel with a channel fixture gets these badges: it overrides
            // subscriber/0 and adds subscriber/3 and bits/100, subscriber/12 uses the global
            // catalog because the channel omits that version, and subscriber/99 keeps the chip.
            if (summary.Channel == MockBadgeSource.ChannelLogin)
            {
                Append(store, summary.Channel, baseAt, 18, "seed-channel-0",
                    "Channel subscriber badge", "subscriber", "0");
                Append(store, summary.Channel, baseAt, 21, "seed-channel-3",
                    "Channel subscriber months", "subscriber", "3");
                Append(store, summary.Channel, baseAt, 24, "seed-bits",
                    "Channel bits badge", "bits", "100");
                Append(store, summary.Channel, baseAt, 28, "seed-global-12",
                    "Global subscriber version", "subscriber", "12");
                Append(store, summary.Channel, baseAt, 31, "seed-unmatched",
                    "Unmatched badge version", "subscriber", "99");
            }
            store.CloseSession(summary.Id, finalize: true);
            logger.LogInformation("Seeded chat for {Session}.", summary.Id);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void Append(
        ChatLogStore store,
        string channel,
        long baseAt,
        int offsetSeconds,
        string id,
        string text,
        string? badgeSet,
        string? badgeVersion,
        Chatter? chatter = null)
    {
        Chatter who = chatter ?? Chatter.Viewer;
        var message = new ChatMessage(
            id,
            channel,
            baseAt + (offsetSeconds * 1000L),
            AtIsLocal: false,
            UserId: who.UserId,
            Login: who.Login,
            DisplayName: who.DisplayName,
            Color: who.Color,
            Badges: badgeSet is null
                ? []
                : [new ChatBadge(badgeSet, badgeVersion!, null)],
            Emotes: text.Contains("Kappa", StringComparison.Ordinal)
                ? [new ChatEmote("25", [new ChatEmoteRange(0, 4)])]
                : [],
            Bits: null,
            Reply: null,
            Action: false,
            Text: text);

        store.Append(channel, new ChatSourceEvent.MessageEvent(message));
    }

    private sealed record Chatter(string UserId, string Login, string DisplayName, string Color)
    {
        public static Chatter Viewer { get; } = new("mock-viewer", "mock_viewer", "MockViewer", "#9146FF");

        // A Twitch colour unlike every channel colour, so the chat visibly overrides it.
        public static Chatter Streamer(string login) => new($"mock-{login}", login, login, "#1E90FF");
    }
}
