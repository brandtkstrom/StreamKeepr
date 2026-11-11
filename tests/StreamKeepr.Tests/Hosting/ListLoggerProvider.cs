using Microsoft.Extensions.Logging;

namespace StreamKeepr.Tests.Hosting;

internal sealed class ListLoggerProvider : ILoggerProvider
{
    public List<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new ListLogger(this);

    public void Dispose()
    {
    }

    private sealed class ListLogger(ListLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Messages.Add(formatter(state, exception));
    }
}
