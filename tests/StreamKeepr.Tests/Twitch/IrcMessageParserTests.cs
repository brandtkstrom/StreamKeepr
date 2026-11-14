using StreamKeepr.Infrastructure.Twitch.Chat;

namespace StreamKeepr.Tests.Twitch;

public sealed class IrcMessageParserTests
{
    [Fact]
    public void Parses_tags_prefix_command_and_trailing()
    {
        const string line =
            "@badge-info=;badges=moderator/1;color=#00FF00;display-name=Alice;emotes=25:0-4;" +
            "id=abc;tmi-sent-ts=1507246572675;user-id=42 " +
            ":alice!alice@alice.tmi.twitch.tv PRIVMSG #chan :Kappa hi";

        Assert.True(IrcMessageParser.TryParse(line, out IrcMessage? message));
        Assert.NotNull(message);
        Assert.Equal("PRIVMSG", message.Command);
        Assert.Equal("#chan", message.Parameter(0));
        Assert.Equal("Kappa hi", message.Trailing);
        Assert.Equal("moderator/1", message.Tag("badges"));
        Assert.Equal("alice!alice@alice.tmi.twitch.tv", message.Prefix);
    }

    [Fact]
    public void Unescapes_tags_and_drops_an_invalid_escape_backslash()
    {
        Assert.Equal("hello world;test\\ and b", IrcTagParser.Unescape(
            "hello\\sworld\\:test\\\\\\sand \\b"));
    }

    [Fact]
    public void Rejects_malformed_lines_without_throwing()
    {
        Assert.False(IrcMessageParser.TryParse("", out _));
        Assert.False(IrcMessageParser.TryParse("@tags-only", out _));
    }
}
