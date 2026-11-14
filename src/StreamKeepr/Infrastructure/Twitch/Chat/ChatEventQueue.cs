using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>
/// The bounded hand-off from a chat source to the capture service. When the consumer falls
/// behind, a channel's new events go to a small per-channel backlog instead of growing memory
/// without limit: messages and chat log events are counted as one lost range, reported as a
/// <see cref="ChatSourceEvent.Dropped"/>, while connection events are kept in order because the
/// capture service needs them to write outage gaps. The backlog drains as the reader frees room.
/// </summary>
public sealed class ChatEventQueue
{
    /// <summary>About 3 minutes of a 50 msg/s chat; the capture service drains far faster.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly Channel<ChatSourceEvent> _channel;
    private readonly Dictionary<string, Backlog> _backlogs = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _completed;

    public ChatEventQueue(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _channel = Channel.CreateBounded<ChatSourceEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Queues an event without waiting; a full queue backlogs it as described above.</summary>
    public void Write(ChatSourceEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_gate)
        {
            if (_completed)
                return;

            // A channel with a backlog stays in order: nothing jumps ahead of it.
            if (_backlogs.TryGetValue(evt.Channel, out Backlog? backlog) && !TryFlush(evt.Channel, backlog))
            {
                backlog.Add(evt);
                return;
            }

            if (!_channel.Writer.TryWrite(evt))
            {
                backlog = new Backlog();
                backlog.Add(evt);
                _backlogs[evt.Channel] = backlog;
            }
        }
    }

    public async IAsyncEnumerable<ChatSourceEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (await _channel.Reader.WaitToReadAsync(ct))
        {
            while (_channel.Reader.TryRead(out ChatSourceEvent? evt))
            {
                FlushBacklogs();
                yield return evt;
            }
        }
    }

    /// <summary>
    /// Ends the reader once queued events are consumed; later writes are ignored. A backlog
    /// still pending at completion is discarded: completion happens only at shutdown.
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            _backlogs.Clear();
            _channel.Writer.TryComplete();
        }
    }

    private void FlushBacklogs()
    {
        lock (_gate)
        {
            foreach ((string channel, Backlog backlog) in _backlogs.ToArray())
                TryFlush(channel, backlog);
        }
    }

    /// <summary>Writes as much of the backlog as fits; true when it is empty and removed.</summary>
    private bool TryFlush(string channel, Backlog backlog)
    {
        while (backlog.TryPeek(channel, out ChatSourceEvent? next))
        {
            if (!_channel.Writer.TryWrite(next))
                return false;

            backlog.Pop();
        }

        _backlogs.Remove(channel);
        return true;
    }

    /// <summary>
    /// One channel's pending events, oldest first. Consecutive lossy events collapse into one
    /// drop range; consecutive disconnects keep the first, which is when the outage began.
    /// </summary>
    private sealed class Backlog
    {
        private readonly LinkedList<Item> _items = new();

        public void Add(ChatSourceEvent evt)
        {
            Item? last = _items.Last?.Value;
            switch (evt)
            {
                case ChatSourceEvent.Disconnected when last?.Event is ChatSourceEvent.Disconnected:
                    return;
                case ChatSourceEvent.Disconnected or ChatSourceEvent.Joined:
                    _items.AddLast(new Item(evt, null));
                    return;
                case ChatSourceEvent.Dropped dropped when last?.Drop is { } open:
                    open.Add(dropped.FromMs, dropped.ToMs, dropped.Count);
                    return;
                case ChatSourceEvent.Dropped dropped:
                    _items.AddLast(new Item(null, new Range(dropped.FromMs, dropped.ToMs, dropped.Count)));
                    return;
                default:
                    if (last?.Drop is { } range)
                        range.Add(evt.AtMs, evt.AtMs, 1);
                    else
                        _items.AddLast(new Item(null, new Range(evt.AtMs, evt.AtMs, 1)));
                    return;
            }
        }

        public bool TryPeek(string channel, [NotNullWhen(true)] out ChatSourceEvent? next)
        {
            next = _items.First?.Value switch
            {
                null => null,
                { Event: { } evt } => evt,
                { Drop: { } drop } => new ChatSourceEvent.Dropped(
                    channel, drop.ToMs, drop.FromMs, drop.ToMs, drop.Count),
                _ => null,
            };
            return next is not null;
        }

        public void Pop() => _items.RemoveFirst();

        private sealed record Item(ChatSourceEvent? Event, Range? Drop);

        private sealed class Range(long fromMs, long toMs, int count)
        {
            public long FromMs { get; private set; } = fromMs;

            public long ToMs { get; private set; } = toMs;

            public int Count { get; private set; } = count;

            public void Add(long fromMs, long toMs, int count)
            {
                FromMs = Math.Min(FromMs, fromMs);
                ToMs = Math.Max(ToMs, toMs);
                Count += count;
            }
        }
    }
}
