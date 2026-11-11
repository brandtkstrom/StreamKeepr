using Microsoft.Extensions.Logging;

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
}
