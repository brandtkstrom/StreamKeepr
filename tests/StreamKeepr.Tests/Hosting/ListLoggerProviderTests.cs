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
    }
}
