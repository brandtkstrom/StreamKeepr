using System.Collections.Concurrent;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>The outcome of <see cref="ChatSourceChannels.Join"/>.</summary>
public enum ChatJoinResult
{
    Joined,
    InvalidLogin,
    LimitReached,
}

/// <summary>
/// The state every <see cref="IChatSource"/> keeps: the set of channels it should be joined to
/// and the <see cref="ChatEventQueue"/> it hands events to the capture service through. Sources
/// compose one instead of sharing a base class; each still owns its own connection behaviour.
/// </summary>
public sealed class ChatSourceChannels(int maxChannels = int.MaxValue)
{
    private readonly ConcurrentDictionary<string, byte> _joined = new(StringComparer.Ordinal);
    private readonly ChatEventQueue _events = new();

    public int MaxChannels => maxChannels;

    /// <summary>A live view of the joined logins; safe to enumerate while joins change.</summary>
    public IEnumerable<string> Joined => _joined.Keys;

    public bool Contains(string login) => _joined.ContainsKey(login);

    /// <summary>
    /// Adds the normalized <paramref name="channel"/> unless it is not a valid login or the set
    /// is full. Re-joining a channel already in the set always succeeds.
    /// </summary>
    public ChatJoinResult Join(string channel, out string login)
    {
        login = TwitchLogin.Normalize(channel);
        if (!TwitchLogin.IsValid(login))
            return ChatJoinResult.InvalidLogin;

        if (_joined.Count >= maxChannels && !_joined.ContainsKey(login))
            return ChatJoinResult.LimitReached;

        _joined[login] = 0;
        return ChatJoinResult.Joined;
    }

    /// <summary>Removes the normalized <paramref name="channel"/> and returns that login.</summary>
    public string Part(string channel)
    {
        string login = TwitchLogin.Normalize(channel);
        _joined.TryRemove(login, out _);
        return login;
    }

    public void Write(ChatSourceEvent evt) => _events.Write(evt);

    public IAsyncEnumerable<ChatSourceEvent> ReadAllAsync(CancellationToken ct) =>
        _events.ReadAllAsync(ct);

    /// <summary>Ends the reader; see <see cref="ChatEventQueue.Complete"/>.</summary>
    public void Complete() => _events.Complete();
}
