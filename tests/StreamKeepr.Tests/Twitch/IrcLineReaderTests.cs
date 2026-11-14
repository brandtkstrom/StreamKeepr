using System.Text;
using StreamKeepr.Infrastructure.Twitch.Chat;

namespace StreamKeepr.Tests.Twitch;

public sealed class IrcLineReaderTests
{
    [Fact]
    public async Task A_crlf_split_across_frames_still_ends_the_line()
    {
        var socket = new ScriptedSocket("PING :a\r", "\nPING :b\r\n");

        List<string> lines = await ReadAllAsync(socket);

        Assert.Equal(["PING :a", "PING :b"], lines);
    }

    [Fact]
    public async Task A_line_split_mid_text_is_joined()
    {
        var socket = new ScriptedSocket("PRIVMSG #chan :hel", "lo\r\nPING :x\r\n");

        List<string> lines = await ReadAllAsync(socket);

        Assert.Equal(["PRIVMSG #chan :hello", "PING :x"], lines);
    }

    [Fact]
    public async Task Several_lines_in_one_frame_are_all_returned()
    {
        var socket = new ScriptedSocket("PING :a\r\nPING :b\nPING :c\r\n");

        List<string> lines = await ReadAllAsync(socket);

        Assert.Equal(["PING :a", "PING :b", "PING :c"], lines);
    }

    private static async Task<List<string>> ReadAllAsync(IIrcSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lines = new List<string>();
        await foreach (string line in IrcLineReader.ReadLinesAsync(socket, timeout.Token))
            lines.Add(line);
        return lines;
    }

    private sealed class ScriptedSocket(params string[] frames) : IIrcSocket
    {
        private readonly Queue<string> _frames = new(frames);

        public Task ConnectAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;

        public Task<IrcSocketFrame> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (!_frames.TryDequeue(out string? frame))
                return Task.FromResult(new IrcSocketFrame(0, Closed: true));

            byte[] bytes = Encoding.UTF8.GetBytes(frame);
            bytes.CopyTo(buffer.Span);
            return Task.FromResult(new IrcSocketFrame(bytes.Length, Closed: false));
        }

        public Task SendTextAsync(string text, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
