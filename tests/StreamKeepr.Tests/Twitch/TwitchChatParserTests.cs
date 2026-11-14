using System.Text;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Infrastructure.Twitch.Chat;

namespace StreamKeepr.Tests.Twitch;

public sealed class TwitchChatParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 18, 0, 0, TimeSpan.Zero);

    private static IrcMessage ParseLine(string line)
    {
        Assert.True(IrcMessageParser.TryParse(line, out IrcMessage? message));
        return message!;
    }

    private static TwitchChatParser Parser() => new(new FakeTimeProvider(Now));

    [Fact]
    public void Maps_a_privmsg_with_badges_emotes_and_reply_metadata()
    {
        IrcMessage message = ParseLine(
            "@badge-info=subscriber/12;badges=moderator/1,subscriber/12;color=#00FF00;" +
            "display-name=Alice;emotes=25:6-10;id=abc;reply-parent-msg-id=parent;" +
            "reply-parent-user-login=bob;reply-parent-display-name=Bob;" +
            "reply-parent-msg-body=hello\\sworld;tmi-sent-ts=1507246572675;user-id=42 " +
            ":alice!alice@alice.tmi.twitch.tv PRIVMSG #chan :hello Kappa");

        ChatSourceEvent.MessageEvent evt = Assert.IsType<ChatSourceEvent.MessageEvent>(
            Assert.Single(Parser().Parse(message)));
        ChatMessage chat = evt.Message;

        Assert.Equal("abc", chat.Id);
        Assert.Equal("chan", chat.Channel);
        Assert.Equal(1507246572675, chat.AtMs);
        Assert.Equal("alice", chat.Login);
        Assert.Equal("Alice", chat.DisplayName);
        Assert.Equal("42", chat.UserId);
        Assert.Equal(["moderator", "subscriber"],
            chat.Badges.Select(badge => badge.Set));
        Assert.Equal("12", chat.Badges[1].Info);
        Assert.Equal(6, chat.Emotes.Single().Ranges.Single().Start);
        Assert.Equal(10, chat.Emotes.Single().Ranges.Single().End);
        Assert.Equal("hello world", chat.Reply?.Text);
        Assert.Equal("Kappa", chat.Text[6..]);
    }

    [Fact]
    public void Uses_code_point_ranges_for_an_emoji_before_an_emote()
    {
        IrcMessage message = ParseLine(
            "@emotes=25:2-6;id=abc;tmi-sent-ts=1507246572675;user-id=42 " +
            ":alice!alice@alice.tmi.twitch.tv PRIVMSG #chan :😀 Kappa");

        ChatMessage chat = Assert.IsType<ChatSourceEvent.MessageEvent>(
            Assert.Single(Parser().Parse(message))).Message;

        ChatEmoteRange range = chat.Emotes.Single().Ranges.Single();
        Assert.Equal(2, range.Start);
        Assert.Equal(6, range.End);
        Assert.Equal("Kappa", string.Concat(chat.Text.EnumerateRunes()
            .Skip(range.Start)
            .Take(range.End - range.Start + 1)
            .Select(rune => rune.ToString())));
    }

    [Fact]
    public void Strips_action_markers_and_adjusts_emote_ranges()
    {
        IrcMessage message = ParseLine(
            "@emotes=25:8-12;id=abc;tmi-sent-ts=1507246572675;user-id=42 " +
            ":alice!alice@alice.tmi.twitch.tv PRIVMSG #chan :\u0001ACTION Kappa hi\u0001");

        ChatMessage chat = Assert.IsType<ChatSourceEvent.MessageEvent>(
            Assert.Single(Parser().Parse(message))).Message;

        Assert.True(chat.Action);
        Assert.Equal("Kappa hi", chat.Text);
        ChatEmoteRange range = chat.Emotes.Single().Ranges.Single();
        Assert.Equal(0, range.Start);
        Assert.Equal(4, range.End);
    }

    [Fact]
    public void Falls_back_to_local_time_when_tmi_sent_ts_is_missing()
    {
        IrcMessage message = ParseLine(
            "@id=abc;user-id=42 :alice!alice@alice.tmi.twitch.tv PRIVMSG #chan :hello");

        ChatMessage chat = Assert.IsType<ChatSourceEvent.MessageEvent>(
            Assert.Single(Parser().Parse(message))).Message;

        Assert.True(chat.AtIsLocal);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), chat.AtMs);
    }

    [Fact]
    public void Maps_clear_events()
    {
        IrcMessage clearMessage = ParseLine(
            "@target-msg-id=abc;tmi-sent-ts=1507246572675 :tmi.twitch.tv CLEARMSG #chan :hello");
        IrcMessage clearChat = ParseLine(
            "@target-user-id=42;tmi-sent-ts=1507246572676 :tmi.twitch.tv CLEARCHAT #chan :alice");

        Assert.IsType<ChatSourceEvent.Delete>(Assert.Single(Parser().Parse(clearMessage)));
        ChatSourceEvent.Clear clear = Assert.IsType<ChatSourceEvent.Clear>(
            Assert.Single(Parser().Parse(clearChat)));
        Assert.Equal("42", clear.TargetUserId);
        Assert.Equal("alice", clear.TargetLogin);
    }
}
