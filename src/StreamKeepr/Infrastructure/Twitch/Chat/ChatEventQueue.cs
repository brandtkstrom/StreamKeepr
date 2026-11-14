using System.Threading.Channels;

namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>
/// The bounded hand-off from a chat source to the capture service. When the consumer falls
/// behind, new events are dropped and counted per channel instead of growing memory without
/// limit; the next accepted write for that channel is preceded by one
/// <see cref="ChatSourceEvent.Dropped"/> covering the lost range.
/// </summary>
public sealed class ChatEventQueue
{
    /// <summary>About 3 minutes of a 50 msg/s chat; the capture service drains far faster.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly Channel<ChatSourceEvent> _channel;
    private readonly Dictionary<string, PendingDrop> _drops = new(StringComparer.Ordinal);
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

    /// <summary>Queues an event without waiting; a full queue drops it and records the loss.</summary>
    public void Write(ChatSourceEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_gate)
        {
            if (_completed)
                return;

            if (_drops.TryGetValue(evt.Channel, out PendingDrop? drop))
            {
                var report = new ChatSourceEvent.Dropped(
                    evt.Channel, drop.ToMs, drop.FromMs, drop.ToMs, drop.Count);
                if (!_channel.Writer.TryWrite(report))
                {
                    drop.Add(evt.AtMs);
                    return;
                }

                _drops.Remove(evt.Channel);
            }

            if (!_channel.Writer.TryWrite(evt))
            {
                if (_drops.TryGetValue(evt.Channel, out PendingDrop? pending))
                    pending.Add(evt.AtMs);
                else
                    _drops[evt.Channel] = new PendingDrop(evt.AtMs);
            }
        }
    }

    public IAsyncEnumerable<ChatSourceEvent> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    /// <summary>Ends the reader once queued events are consumed; later writes are ignored.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            _channel.Writer.TryComplete();
        }
    }

    private sealed class PendingDrop(long atMs)
    {
        public long FromMs { get; private set; } = atMs;

        public long ToMs { get; private set; } = atMs;

        public int Count { get; private set; } = 1;

        public void Add(long atMs)
        {
            FromMs = Math.Min(FromMs, atMs);
            ToMs = Math.Max(ToMs, atMs);
            Count++;
        }
    }
}
