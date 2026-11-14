using StreamKeepr.Infrastructure.Twitch.Chat;

namespace StreamKeepr.Tests.Twitch;

public sealed class ChatEventQueueTests
{
    [Fact]
    public async Task A_full_queue_drops_new_events_and_reports_the_lost_range_once_it_drains()
    {
        var queue = new ChatEventQueue(capacity: 2);
        queue.Write(Delete("chan", 1_000));
        queue.Write(Delete("chan", 2_000));
        queue.Write(Delete("chan", 3_000));
        queue.Write(Delete("chan", 4_000));

        List<ChatSourceEvent> first = await TakeAsync(queue, 2);
        queue.Write(Delete("chan", 5_000));
        List<ChatSourceEvent> second = await TakeAsync(queue, 2);

        Assert.Equal([1_000L, 2_000L], first.Select(evt => evt.AtMs));
        var dropped = Assert.IsType<ChatSourceEvent.Dropped>(second[0]);
        Assert.Equal("chan", dropped.Channel);
        Assert.Equal(3_000, dropped.FromMs);
        Assert.Equal(4_000, dropped.ToMs);
        Assert.Equal(2, dropped.Count);
        Assert.Equal(5_000, second[1].AtMs);
    }

    [Fact]
    public async Task Drops_are_reported_per_channel()
    {
        var queue = new ChatEventQueue(capacity: 1);
        queue.Write(Delete("a", 1_000));
        queue.Write(Delete("b", 2_000));

        await TakeAsync(queue, 1);
        queue.Write(Delete("a", 3_000));
        var aFirst = await TakeAsync(queue, 1);
        Assert.Equal(3_000, aFirst[0].AtMs);

        queue.Write(Delete("b", 4_000));
        var bDropped = Assert.IsType<ChatSourceEvent.Dropped>((await TakeAsync(queue, 1))[0]);
        Assert.Equal("b", bDropped.Channel);
        Assert.Equal(1, bDropped.Count);
    }

    [Fact]
    public async Task Complete_ends_the_reader()
    {
        var queue = new ChatEventQueue(capacity: 4);
        queue.Write(Delete("chan", 1_000));
        queue.Complete();
        queue.Write(Delete("chan", 2_000));

        var all = new List<ChatSourceEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (ChatSourceEvent evt in queue.ReadAllAsync(timeout.Token))
            all.Add(evt);

        Assert.Equal([1_000L], all.Select(evt => evt.AtMs));
    }

    private static ChatSourceEvent.Delete Delete(string channel, long at) =>
        new(channel, at, $"{channel}-{at}");

    private static async Task<List<ChatSourceEvent>> TakeAsync(ChatEventQueue queue, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taken = new List<ChatSourceEvent>();
        await foreach (ChatSourceEvent evt in queue.ReadAllAsync(timeout.Token))
        {
            taken.Add(evt);
            if (taken.Count == count)
                break;
        }

        return taken;
    }
}
