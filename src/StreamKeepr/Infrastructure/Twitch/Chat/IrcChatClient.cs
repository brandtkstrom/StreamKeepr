using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>
/// A read-only Twitch IRC client. Chat is a live tap: it reconnects and re-joins channels but
/// cannot replay messages missed while disconnected.
/// </summary>
public sealed class IrcChatClient(
    IIrcSocketFactory sockets,
    TwitchChatParser parser,
    IOptions<ChatOptions> options,
    IRetryJitter jitter,
    TimeProvider clock,
    ILogger<IrcChatClient> logger) : BackgroundService, IChatSource
{
    private static readonly Uri Endpoint = new("wss://irc-ws.chat.twitch.tv:443");
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromSeconds(5);

    /// <summary>Anonymous auth or capability rejection is not transient; retry slowly.</summary>
    private static readonly TimeSpan AuthRetryDelay = TimeSpan.FromMinutes(1);

    private readonly ChatSourceChannels _channels = new(options.Value.MaxChannels);
    private readonly ConcurrentDictionary<string, byte> _sent = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _state = (int)ChatConnectionState.Disabled;
    private int _tokenDisabled;

    public string Source => UseToken
        ? ChatSources.Token
        : ChatSources.Anonymous;

    private bool UseToken => options.Value.HasToken && Volatile.Read(ref _tokenDisabled) == 0;

    public ValueTask JoinAsync(string channel, CancellationToken ct)
    {
        if (_channels.Join(channel, out string login) == ChatJoinResult.LimitReached)
        {
            logger.LogWarning(
                "Chat join limit of {Limit} channels reached; {Channel} is not joined.",
                _channels.MaxChannels, login);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask PartAsync(string channel, CancellationToken ct)
    {
        _channels.Part(channel);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<ChatSourceEvent> ReadAllAsync(CancellationToken ct) =>
        _channels.ReadAllAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            SetState(ChatConnectionState.Disabled);
            _channels.Complete();
            return;
        }

        TimeSpan backoff = MinimumBackoff;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool immediate = false;
                bool connected = false;
                try
                {
                    connected = await RunConnectionAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IrcReconnectException)
                {
                    immediate = true;
                }
                catch (IrcAuthException ex)
                {
                    logger.LogWarning("Twitch IRC authentication failed: {Reason}", ex.Message);
                    if (UseToken)
                    {
                        Interlocked.Exchange(ref _tokenDisabled, 1);
                        logger.LogWarning("Falling back to anonymous Twitch IRC capture.");
                        continue;
                    }

                    SetState(ChatConnectionState.Unavailable);
                    await Task.Delay(AuthRetryDelay, clock, stoppingToken);
                    continue;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Twitch IRC connection failed; reconnecting.");
                }

                EmitDisconnected();

                if (connected)
                    backoff = MinimumBackoff;

                if (!immediate)
                {
                    SetState(ChatConnectionState.Backoff);
                    TimeSpan delay = jitter.Apply(backoff);
                    logger.LogInformation("Twitch IRC reconnecting in {Delay}.", delay);
                    await Task.Delay(delay, clock, stoppingToken);
                    backoff = TimeSpan.FromSeconds(Math.Min(
                        backoff.TotalSeconds * 2,
                        options.Value.ReconnectMaxSeconds));
                }
            }
        }
        finally
        {
            SetState(ChatConnectionState.Idle);
            _channels.Complete();
        }
    }

    private async Task<bool> RunConnectionAsync(CancellationToken ct)
    {
        SetState(ChatConnectionState.Connecting);
        await using IIrcSocket socket = sockets.Create();

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(options.Value.ConnectTimeoutSeconds));
            await socket.ConnectAsync(Endpoint, connectCts.Token);
        }

        SetState(ChatConnectionState.Registering);
        bool useToken = UseToken;
        string nick = useToken
            ? TwitchLogin.Normalize(options.Value.TwitchLogin)
            : NewAnonymousNick();

        await SendAsync(socket, "CAP LS 302", ct);
        if (useToken)
            await SendAsync(socket, $"PASS oauth:{options.Value.TwitchUserOAuthToken}", ct);
        await SendAsync(socket, $"NICK {nick}", ct);
        await SendAsync(socket, $"USER {nick} 0 * :{nick}", ct);
        await SendAsync(socket, "CAP REQ :twitch.tv/tags twitch.tv/commands", ct);
        await SendAsync(socket, "CAP END", ct);

        _sent.Clear();
        using var registration = CancellationTokenSource.CreateLinkedTokenSource(ct);
        registration.CancelAfter(TimeSpan.FromSeconds(options.Value.RegistrationTimeoutSeconds));

        using var joins = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? joinPump = null;
        bool welcomed = false;

        try
        {
            await foreach (string line in IrcLineReader.ReadLinesAsync(
                               socket, registration.Token))
            {
                if (!IrcMessageParser.TryParse(line, out IrcMessage? message) || message is null)
                {
                    logger.LogDebug("Skipping malformed IRC line.");
                    continue;
                }

                if (message.Command == "PING")
                {
                    await SendAsync(socket, $"PONG :{Sanitize(message.Trailing)}", ct);
                    continue;
                }

                if (message.Command == "RECONNECT")
                {
                    throw new IrcReconnectException();
                }

                if (message.Command == "001")
                {
                    welcomed = true;
                    registration.CancelAfter(Timeout.InfiniteTimeSpan);
                    SetState(ChatConnectionState.Joining);
                    joinPump ??= JoinPumpAsync(socket, joins.Token);
                    continue;
                }

                if (message.Command == "433")
                {
                    if (UseToken)
                    {
                        throw new IrcAuthException(
                            "The configured chat login is already connected.");
                    }

                    throw new IrcReconnectException();
                }

                if (message.Command == "NOTICE" &&
                    IsAuthFailure(message.Trailing))
                {
                    throw new IrcAuthException(message.Trailing ?? "Authentication failed.");
                }

                if (message.Command == "CAP" &&
                    string.Equals(message.Parameter(1), "NAK",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IrcAuthException("Twitch rejected the required IRC capabilities.");
                }

                foreach (ChatSourceEvent evt in parser.Parse(message))
                {
                    _channels.Write(evt);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !welcomed)
        {
            throw new TimeoutException("Twitch IRC registration timed out.");
        }
        finally
        {
            joins.Cancel();
            if (joinPump is not null)
            {
                try
                {
                    await joinPump;
                }
                catch (OperationCanceledException)
                {
                    // The connection is closing.
                }
            }
        }

        return welcomed;
    }

    private async Task JoinPumpAsync(IIrcSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool sentAny = false;
            foreach (string channel in _channels.Joined)
            {
                if (_sent.ContainsKey(channel))
                    continue;

                await SendAsync(socket, $"JOIN #{channel}", ct);
                _sent[channel] = 0;
                _channels.Write(new ChatSourceEvent.Joined(
                    channel, clock.GetUtcNow().ToUnixTimeMilliseconds()));
                sentAny = true;
                await Task.Delay(TimeSpan.FromMilliseconds(options.Value.JoinIntervalMs), clock, ct);
                break;
            }

            foreach (string channel in _sent.Keys)
            {
                if (_channels.Contains(channel))
                    continue;

                await SendAsync(socket, $"PART #{channel}", ct);
                _sent.TryRemove(channel, out _);
            }

            if (!sentAny)
                await Task.Delay(TimeSpan.FromMilliseconds(options.Value.JoinIntervalMs), clock, ct);
        }
    }

    private async Task SendAsync(IIrcSocket socket, string text, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await socket.SendTextAsync(text + "\r\n", ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void EmitDisconnected()
    {
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (string channel in _channels.Joined)
        {
            _channels.Write(new ChatSourceEvent.Disconnected(channel, now));
        }
    }

    private void SetState(ChatConnectionState state)
    {
        int previous = Interlocked.Exchange(ref _state, (int)state);
        if (previous == (int)state)
            return;

        logger.LogInformation(
            "Twitch IRC state {Previous} -> {State}.",
            (ChatConnectionState)previous, state);
    }

    private static bool IsAuthFailure(string? text) =>
        text is not null &&
        (text.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Improperly formatted auth", StringComparison.OrdinalIgnoreCase));

    private static string NewAnonymousNick() =>
        "justinfan" + Random.Shared.Next(10000, 99999).ToString(
            System.Globalization.CultureInfo.InvariantCulture);

    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        string sanitized = text.Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);
        return sanitized.Length > 450 ? sanitized[..450] : sanitized;
    }

    public ValueTask DisposeAsync()
    {
        _channels.Complete();
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class IrcReconnectException : Exception;

    private sealed class IrcAuthException(string message) : Exception(message);
}
