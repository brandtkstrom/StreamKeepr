using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>
/// Maps Twitch IRC commands and tags into the small capture model. It never throws on malformed
/// input; invalid fields are dropped and the message continues with its remaining data.
/// </summary>
public sealed partial class TwitchChatParser(TimeProvider clock)
{
    private const string ActionPrefix = "\u0001ACTION ";
    private const int ActionPrefixCodePoints = 8;

    public IReadOnlyList<ChatSourceEvent> Parse(IrcMessage message)
    {
        string command = message.Command;
        if (command == "PRIVMSG")
        {
            return ParsePrivMessage(message) is { } chat
                ? [new ChatSourceEvent.MessageEvent(chat)]
                : [];
        }

        if (command == "CLEARMSG")
            return ParseClearMessage(message);
        if (command == "CLEARCHAT")
            return ParseClearChat(message);
        if (command == "ROOMSTATE")
            return ParseRoomState(message);
        if (command == "NOTICE")
            return ParseNotice(message);

        return [];
    }

    private ChatMessage? ParsePrivMessage(IrcMessage message)
    {
        string channel = ChannelOf(message);
        if (channel.Length == 0)
            return null;

        long at = AtOf(message, out bool atIsLocal);
        string login = LoginOf(message);
        if (login.Length == 0)
            return null;

        string text = message.Trailing ?? "";
        bool action = text.StartsWith(ActionPrefix, StringComparison.Ordinal) &&
                      text.EndsWith('\u0001');
        if (action)
        {
            text = text[ActionPrefix.Length..];
            if (text.EndsWith('\u0001'))
                text = text[..^1];
        }

        int codePoints = CountCodePoints(text);
        var emotes = ParseEmotes(message.Tag("emotes"), action ? ActionPrefixCodePoints : 0, codePoints);
        var badges = ParseBadges(message);
        ChatReply? reply = ParseReply(message);

        return new ChatMessage(
            message.Tag("id") ?? "",
            channel,
            at,
            atIsLocal,
            EmptyToNull(message.Tag("user-id")),
            login,
            EmptyToNull(message.Tag("display-name")) ?? login,
            EmptyToNull(message.Tag("color")),
            badges,
            emotes,
            ParseInt(message.Tag("bits")),
            reply,
            action,
            text);
    }

    private IReadOnlyList<ChatSourceEvent> ParseClearMessage(IrcMessage message)
    {
        string channel = ChannelOf(message);
        string? target = EmptyToNull(message.Tag("target-msg-id"));
        if (channel.Length == 0 || target is null)
            return [];

        return [new ChatSourceEvent.Delete(channel, AtOf(message, out _), target)];
    }

    private IReadOnlyList<ChatSourceEvent> ParseClearChat(IrcMessage message)
    {
        string channel = ChannelOf(message);
        if (channel.Length == 0)
            return [];

        return
        [
            new ChatSourceEvent.Clear(
                channel,
                AtOf(message, out _),
                EmptyToNull(message.Tag("target-user-id")),
                EmptyToNull(message.Trailing)),
        ];
    }

    private IReadOnlyList<ChatSourceEvent> ParseRoomState(IrcMessage message)
    {
        string channel = ChannelOf(message);
        if (channel.Length == 0)
            return [];

        return
        [
            new ChatSourceEvent.RoomState(
                channel,
                AtOf(message, out _),
                message.Tag("emote-only") == "1",
                message.Tag("subs-only") == "1",
                message.Tag("r9k") == "1",
                ParseInt(message.Tag("slow")) ?? 0),
        ];
    }

    private IReadOnlyList<ChatSourceEvent> ParseNotice(IrcMessage message)
    {
        string channel = ChannelOf(message);
        if (channel.Length == 0)
            return [];

        return
        [
            new ChatSourceEvent.Notice(
                channel,
                AtOf(message, out _),
                EmptyToNull(message.Tag("msg-id")),
                message.Trailing ?? ""),
        ];
    }

    private static string ChannelOf(IrcMessage message)
    {
        string channel = message.Parameter(0) ?? "";
        return channel.StartsWith('#') ? channel[1..] : channel;
    }

    private static string LoginOf(IrcMessage message)
    {
        string prefix = message.Prefix ?? "";
        int bang = prefix.IndexOf('!');
        return bang > 0 ? prefix[..bang] : prefix;
    }

    private long AtOf(IrcMessage message, out bool local)
    {
        string? raw = message.Tag("tmi-sent-ts");
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long at) &&
            at > 0)
        {
            local = false;
            return at;
        }

        local = true;
        return clock.GetUtcNow().ToUnixTimeMilliseconds();
    }

    private static IReadOnlyList<ChatBadge> ParseBadges(IrcMessage message)
    {
        var info = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in (message.Tag("badge-info") ?? "").Split(
                     ',', StringSplitOptions.RemoveEmptyEntries))
        {
            int slash = pair.IndexOf('/');
            if (slash > 0)
                info[pair[..slash]] = pair[(slash + 1)..];
        }

        var badges = new List<ChatBadge>();
        foreach (string pair in (message.Tag("badges") ?? "").Split(
                     ',', StringSplitOptions.RemoveEmptyEntries))
        {
            int slash = pair.IndexOf('/');
            if (slash <= 0 || slash == pair.Length - 1)
                continue;

            string set = pair[..slash];
            string version = pair[(slash + 1)..];
            badges.Add(new ChatBadge(set, version, info.GetValueOrDefault(set)));
        }

        return badges;
    }

    private static IReadOnlyList<ChatEmote> ParseEmotes(
        string? raw, int offset, int codePointCount)
    {
        if (string.IsNullOrEmpty(raw))
            return [];

        var emotes = new List<ChatEmote>();
        foreach (string entry in raw.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = entry.IndexOf(':');
            if (colon <= 0 || colon == entry.Length - 1)
                continue;

            string id = entry[..colon];
            if (!EmoteIdPattern().IsMatch(id))
                continue;

            var ranges = new List<ChatEmoteRange>();
            foreach (string range in entry[(colon + 1)..].Split(
                         ',', StringSplitOptions.RemoveEmptyEntries))
            {
                int dash = range.IndexOf('-');
                if (dash <= 0 || dash == range.Length - 1)
                    continue;

                if (!int.TryParse(range[..dash], NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out int start) ||
                    !int.TryParse(range[(dash + 1)..], NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out int end))
                {
                    continue;
                }

                start -= offset;
                end -= offset;
                if (start < 0 || end < start || end >= codePointCount)
                    continue;

                ranges.Add(new ChatEmoteRange(start, end));
            }

            if (ranges.Count > 0)
                emotes.Add(new ChatEmote(id, ranges));
        }

        return emotes;
    }

    private static ChatReply? ParseReply(IrcMessage message)
    {
        string? id = EmptyToNull(message.Tag("reply-parent-msg-id"));
        if (id is null)
            return null;

        string? text = EmptyToNull(message.Tag("reply-parent-msg-body"));
        if (text is { Length: > 500 })
            text = text[..500];

        return new ChatReply(
            id,
            EmptyToNull(message.Tag("reply-parent-user-login")),
            EmptyToNull(message.Tag("reply-parent-display-name")),
            text);
    }

    private static int CountCodePoints(string text)
    {
        int count = 0;
        foreach (Rune _ in text.EnumerateRunes())
            count++;
        return count;
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : null;

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmoteIdPattern();
}
