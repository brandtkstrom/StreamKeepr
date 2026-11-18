using System.Globalization;
using System.Text;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// Makes external process output (Streamlink and FFmpeg stderr) safe to log. The subscriber
/// token is redacted first, then every character that can end or rewrite a log line is escaped:
/// CR, LF, tab, the other C0 controls (including ANSI ESC), DEL, U+0085, U+2028, and U+2029.
/// Serilog's text sink writes string properties raw, so without this a tool line could forge
/// a log entry or drive the terminal.
/// </summary>
public static class ProcessOutput
{
    /// <summary>What the subscriber token is replaced with in logs and diagnostic detail.</summary>
    public const string RedactedToken = "***";

    /// <summary>Redacts <paramref name="token"/> and escapes control characters.</summary>
    public static string ForLog(string line, string? token) => Sanitize(Redact(line, token));

    /// <summary>
    /// Replaces every occurrence of <paramref name="token"/> with <see cref="RedactedToken"/>.
    /// A missing token leaves the text unchanged.
    /// </summary>
    public static string Redact(string text, string? token)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.IsNullOrEmpty(token)
            ? text
            : text.Replace(token, RedactedToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// Escapes CR/LF/tab as <c>\r</c>, <c>\n</c>, <c>\t</c> and other unsafe characters as
    /// <c>\uXXXX</c>. Text without them is returned as the same instance.
    /// </summary>
    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Any(IsUnsafe))
            return text;

        var builder = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                case var unsafeChar when IsUnsafe(unsafeChar):
                    builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }

    private static bool IsUnsafe(char c) =>
        c < (char)0x20 || c == '\u007f' || c == '\u0085' || c == '\u2028' || c == '\u2029';
}
