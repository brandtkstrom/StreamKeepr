using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Media;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Mock;

/// <summary>Seeds deterministic chat logs for the finished Mock recordings.</summary>
public sealed class MockChatSeeder(
    RecordingLibrary library,
    ChatLogStore store,
    IOptions<ChatOptions> options,
    ILogger<MockChatSeeder> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
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
            if (!File.Exists(playlist) || ChatLogLayout.HasChat(directory, logger))
                continue;

            DateTimeOffset? pdt = PlaylistStitcher.FirstProgramDateTime(
                File.ReadAllText(playlist));
            if (pdt is null)
                continue;

            store.OpenSession(summary, ChatSources.Mock);
            long baseAt = pdt.Value.AddSeconds(-options.Value.LeadInSeconds)
                .ToUnixTimeMilliseconds();
            Append(store, summary.Channel, baseAt, 5, "seed-1", "Welcome to the Mock stream",
                "moderator", "1");
            Append(store, summary.Channel, baseAt, 16, "seed-2", "Kappa this is a chat replay",
                null, null);
            Append(store, summary.Channel, baseAt, 27, "seed-3", "PogChamp nice clip",
                null, null);
            store.CloseSession(summary.Id, finalize: true);
            logger.LogInformation("Seeded chat for {Session}.", summary.Id);
        }

        return Task.CompletedTask;
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
        string? badgeVersion)
    {
        var message = new ChatMessage(
            id,
            channel,
            baseAt + (offsetSeconds * 1000L),
            AtIsLocal: false,
            UserId: "mock-viewer",
            Login: "mock_viewer",
            DisplayName: "MockViewer",
            Color: "#9146FF",
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
}
