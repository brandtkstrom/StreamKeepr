using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

public sealed class ProcessOutputTests
{
    private const string Token = "test-oauth-token-27-do-not-log";

    [Fact]
    public void Plain_tool_output_is_logged_unchanged()
    {
        const string line = "[cli][info] Opening stream: 1080p60 (hls) — ünïcode ok";

        Assert.Equal(line, ProcessOutput.ForLog(line, Token));
        Assert.Same(line, ProcessOutput.Sanitize(line));
    }

    [Fact]
    public void Line_breaks_are_escaped_so_output_cannot_forge_a_log_line()
    {
        string forged = ProcessOutput.ForLog(
            "real\r\n[2026-11-17 00:00:00.000 +00:00 ERR] forged\nnext", Token);

        Assert.Equal(
            "real\\r\\n[2026-11-17 00:00:00.000 +00:00 ERR] forged\\nnext",
            forged);
    }

    [Fact]
    public void Ansi_escapes_tabs_and_other_controls_are_escaped()
    {
        string escaped = ProcessOutput.ForLog("\u001b[31mred\u001b[0m\tbell\u0007\u0000del\u007f", Token);

        Assert.Equal("\\u001B[31mred\\u001B[0m\\tbell\\u0007\\u0000del\\u007F", escaped);
    }

    [Fact]
    public void Unicode_line_and_paragraph_separators_are_escaped()
    {
        string escaped = ProcessOutput.ForLog("a\u0085b\u2028c\u2029d", Token);

        Assert.Equal("a\\u0085b\\u2028c\\u2029d", escaped);
    }

    [Fact]
    public void The_subscriber_token_is_redacted_even_when_wrapped_in_control_characters()
    {
        string escaped = ProcessOutput.ForLog(
            $"--twitch-api-header=Authorization=OAuth {Token}\r\n\u001b[0m{Token}", Token);

        Assert.DoesNotContain(Token, escaped, StringComparison.Ordinal);
        Assert.Equal(
            "--twitch-api-header=Authorization=OAuth ***\\r\\n\\u001B[0m***",
            escaped);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_configured_token_output_is_only_sanitized(string? token)
    {
        Assert.Equal("a\\nb", ProcessOutput.ForLog("a\nb", token));
    }

    [Fact]
    public void Escaped_output_never_contains_a_raw_control_or_separator_character()
    {
        string every = new([.. Enumerable.Range(0, 0x100).Select(c => (char)c), '\u2028', '\u2029']);

        string escaped = ProcessOutput.ForLog(every, Token);

        Assert.DoesNotContain(
            escaped,
            c => c < 0x20 || c == 0x7f || c == '\u0085' || c == '\u2028' || c == '\u2029');
    }
}
