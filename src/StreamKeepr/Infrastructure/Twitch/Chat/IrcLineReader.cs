using System.Runtime.CompilerServices;
using System.Text;

namespace StreamKeepr.Infrastructure.Twitch.Chat;

/// <summary>
/// Turns arbitrary socket frames into complete IRC lines. A line may span frames, and one frame
/// may contain any number of CRLF- or LF-delimited lines.
/// </summary>
public static class IrcLineReader
{
    public const int MaxLineBytes = 64 * 1024;

    public static async IAsyncEnumerable<string> ReadLinesAsync(
        IIrcSocket socket,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);

        var buffer = new byte[16 * 1024];
        using var pending = new MemoryStream();
        bool dropping = false;

        while (!ct.IsCancellationRequested)
        {
            IrcSocketFrame frame = await socket.ReceiveAsync(buffer, ct);
            if (frame.Closed)
                yield break;

            int start = 0;
            for (int i = 0; i < frame.Count; i++)
            {
                if (buffer[i] != (byte)'\n')
                    continue;

                int length = i - start;
                if (!dropping && length > 0)
                {
                    pending.Write(buffer, start, length);
                    string line = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length)
                        .TrimEnd('\r');
                    pending.SetLength(0);
                    if (line.Length > 0)
                        yield return line;
                }

                dropping = false;
                start = i + 1;
            }

            if (dropping)
                continue;

            pending.Write(buffer, start, frame.Count - start);
            if (pending.Length > MaxLineBytes)
            {
                pending.SetLength(0);
                dropping = true;
            }
        }
    }
}
