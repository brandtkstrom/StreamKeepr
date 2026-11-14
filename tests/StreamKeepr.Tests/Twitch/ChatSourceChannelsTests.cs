using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch.Chat;

namespace StreamKeepr.Tests.Twitch;

public sealed class ChatSourceChannelsTests
{
    [Fact]
    public void Join_normalizes_the_login_and_records_it()
    {
        var channels = new ChatSourceChannels();

        ChatJoinResult result = channels.Join(" Chan ", out string login);

        Assert.Equal(ChatJoinResult.Joined, result);
        Assert.Equal("chan", login);
        Assert.True(channels.Contains("chan"));
        Assert.Equal(["chan"], channels.Joined);
    }

    [Fact]
    public void Join_rejects_an_invalid_login_without_recording_it()
    {
        var channels = new ChatSourceChannels();

        Assert.Equal(ChatJoinResult.InvalidLogin, channels.Join("not a login!", out _));
        Assert.Empty(channels.Joined);
    }

    [Fact]
    public void Join_stops_at_the_limit_but_a_joined_channel_can_rejoin()
    {
        var channels = new ChatSourceChannels(maxChannels: 1);
        channels.Join("first", out _);

        Assert.Equal(ChatJoinResult.LimitReached, channels.Join("second", out string refused));
        Assert.Equal("second", refused);
        Assert.Equal(ChatJoinResult.Joined, channels.Join("FIRST", out _));
        Assert.Equal(["first"], channels.Joined);
    }

    [Fact]
    public void Part_normalizes_the_login_and_frees_room_under_the_limit()
    {
        var channels = new ChatSourceChannels(maxChannels: 1);
        channels.Join("first", out _);

        Assert.Equal("first", channels.Part(" First "));
        Assert.False(channels.Contains("first"));
        Assert.Equal(ChatJoinResult.Joined, channels.Join("second", out _));
    }

    [Fact]
    public async Task Written_events_are_read_in_order_until_complete()
    {
        var channels = new ChatSourceChannels();
        channels.Write(new ChatSourceEvent.Joined("chan", 1_000));
        channels.Write(new ChatSourceEvent.Disconnected("chan", 2_000));
        channels.Complete();
        channels.Write(new ChatSourceEvent.Joined("chan", 3_000));

        var read = new List<ChatSourceEvent>();
        await foreach (ChatSourceEvent evt in channels.ReadAllAsync(CancellationToken.None))
            read.Add(evt);

        Assert.Equal([1_000L, 2_000L], read.Select(evt => evt.AtMs));
    }
}
