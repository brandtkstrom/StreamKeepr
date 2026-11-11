using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace StreamKeepr.Tests.Hosting;

internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    /// <summary>
    /// A snapshot. Host logging can run on another thread while a test enumerates, so callers
    /// must never observe the live collection.
    /// </summary>
    public IReadOnlyList<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new ListLogger(this);

    public void Dispose()
    {
    }

    private void Add(string message) => _messages.Enqueue(message);

    private sealed class ListLogger(ListLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Add(formatter(state, exception));
    }
}
