using Microsoft.Extensions.Logging;

namespace StreamKeepr.Tests.Hosting;

/// <summary>Loggers that write to a <see cref="ListLoggerProvider"/>, Debug and above.</summary>
internal static class TestLogging
{
    public static ILoggerFactory Factory(ListLoggerProvider logs) =>
        LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
}
