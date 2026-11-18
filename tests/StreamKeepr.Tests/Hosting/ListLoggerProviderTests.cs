using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace StreamKeepr.Tests.Hosting;

public sealed class ListLoggerProviderTests
{
    [Fact]
    public void Messages_is_a_snapshot_that_later_logs_do_not_mutate()
    {
        using var provider = new ListLoggerProvider();
        var logger = provider.CreateLogger("test");

        logger.LogInformation("first");
        IReadOnlyList<string> snapshot = provider.Messages;
        logger.LogInformation("second");

        Assert.Equal(["first"], snapshot);
        Assert.Equal(["first", "second"], provider.Messages);
    }

    [Fact]
    public async Task Wait_completes_for_a_matching_line_logged_before_or_after_the_wait()
    {
        using var provider = new ListLoggerProvider();
        var logger = provider.CreateLogger("test");
        logger.LogInformation("early line");

        Task early = provider.WaitForAsync("early");
        Task late = provider.WaitForAsync("late");
        Assert.True(early.IsCompletedSuccessfully);
        Assert.False(late.IsCompleted);

        logger.LogWarning("a late line");
        await late;

        Assert.Equal(
            [(LogLevel.Information, "early line"), (LogLevel.Warning, "a late line")],
            provider.Entries.Select(entry => (entry.Level, entry.Message)));
    }

    [Fact]
    public void Entries_capture_level_template_properties_and_exception()
    {
        using var provider = new ListLoggerProvider();
        var failure = new InvalidOperationException("boom");

        provider.CreateLogger("test").LogWarning(
            failure, "Refresh failed for {ProviderId}: {HttpStatus} {ChannelId}", "bttv", 503, null);

        CapturedLogEntry entry = Assert.Single(provider.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Refresh failed for bttv: 503 (null)", entry.Message);
        Assert.Equal("Refresh failed for {ProviderId}: {HttpStatus} {ChannelId}", entry.Template);
        Assert.Equal("bttv", entry.Properties["ProviderId"]);
        Assert.Equal(503, entry.Properties["HttpStatus"]);
        Assert.True(entry.Properties.ContainsKey("ChannelId"));
        Assert.Null(entry.Properties["ChannelId"]);
        Assert.False(entry.Properties.ContainsKey("{OriginalFormat}"));
        Assert.Same(failure, entry.Exception);
    }

    [Fact]
    public void Entries_without_a_template_have_no_template_or_properties()
    {
        using var provider = new ListLoggerProvider();

        provider.CreateLogger("test").Log(
            LogLevel.Information, default, "raw state", null, (state, _) => state);

        CapturedLogEntry entry = Assert.Single(provider.Entries);
        Assert.Equal("raw state", entry.Message);
        Assert.Null(entry.Template);
        Assert.Empty(entry.Properties);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Entries_capture_every_active_scope_with_the_innermost_value_winning()
    {
        using var provider = new ListLoggerProvider();
        var logger = provider.CreateLogger("test");

        using (logger.BeginScope(new Dictionary<string, object> { ["Channel"] = "nova", ["Job"] = "outer" }))
        {
            using (logger.BeginScope(new Dictionary<string, object> { ["Job"] = "s1/p001" }))
                await Task.Run(() => logger.LogInformation("inner"));

            logger.LogInformation("outer");
        }

        logger.LogInformation("none");

        Assert.Collection(
            provider.Entries,
            inner =>
            {
                Assert.Equal("nova", inner.Scopes["Channel"]);
                Assert.Equal("s1/p001", inner.Scopes["Job"]);
            },
            outer => Assert.Equal("outer", outer.Scopes["Job"]),
            none => Assert.Empty(none.Scopes));
        Assert.All(provider.Entries, entry => Assert.False(entry.Properties.ContainsKey("Job")));
    }

    [Fact]
    public void Serilog_forwarded_scope_values_arrive_as_properties()
    {
        using var provider = new ListLoggerProvider();
        var providers = new LoggerProviderCollection();
        providers.AddProvider(provider);
        using var serilog = new LoggerConfiguration()
            .Enrich.FromLogContext().WriteTo.Providers(providers).CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog, providerCollection: providers);
        var logger = factory.CreateLogger("test");

        using (logger.BeginScope(new Dictionary<string, object> { ["RecordingId"] = "r1" }))
            logger.LogWarning("Export {ExportId} failed.", "e1");

        CapturedLogEntry entry = Assert.Single(provider.Entries);
        Assert.Equal("e1", entry.Properties["ExportId"]);
        Assert.Equal("r1", entry.Properties["RecordingId"]);
    }

    [Fact]
    public void Serilog_forwarded_messages_render_like_microsoft_extensions_logging()
    {
        using var provider = new ListLoggerProvider();
        var providers = new LoggerProviderCollection();
        providers.AddProvider(provider);
        using var serilog = new LoggerConfiguration().WriteTo.Providers(providers).CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog, providerCollection: providers);

        factory.CreateLogger("test").LogInformation(
            "{Tool}: {Version} took {Seconds:0.0}s for {Channels} (missing {Missing})",
            "ffmpeg", "9.0.2", 1.25, new[] { "nova", "echo" }, null);

        Assert.Equal(["ffmpeg: 9.0.2 took 1.3s for nova, echo (missing (null))"], provider.Messages);
        CapturedLogEntry entry = Assert.Single(provider.Entries);
        Assert.Equal(
            "{Tool}: {Version} took {Seconds:0.0}s for {Channels} (missing {Missing})", entry.Template);
        Assert.Equal("ffmpeg", entry.Properties["Tool"]);
        Assert.Equal(1.25, entry.Properties["Seconds"]);
        Assert.Equal(["nova", "echo"], Assert.IsType<object?[]>(entry.Properties["Channels"]));
        Assert.Null(entry.Properties["Missing"]);
    }
}
