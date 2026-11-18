using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace StreamKeepr.Tests.Hosting;

internal sealed class ListLoggerProvider : ILoggerProvider
{
    private const string OriginalFormatKey = "{OriginalFormat}";
    private static readonly MessageTemplateParser TemplateParser = new();

    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();
    private readonly List<(string Fragment, TaskCompletionSource Logged)> _waiters = [];

    /// <summary>
    /// A snapshot. Host logging can run on another thread while a test enumerates, so callers
    /// must never observe the live collection.
    /// </summary>
    public IReadOnlyList<string> Messages => _entries.Select(entry => entry.Message).ToArray();

    /// <summary>A snapshot of the rendered messages with their levels.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new ListLogger(this);

    /// <summary>
    /// Completes once a message containing <paramref name="fragment"/> has been logged, so a
    /// test can wait for a log line instead of polling for it. Faults after five seconds.
    /// </summary>
    public Task WaitForAsync(string fragment)
    {
        lock (_waiters)
        {
            if (_entries.Any(entry => entry.Message.Contains(fragment, StringComparison.Ordinal)))
                return Task.CompletedTask;

            var logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((fragment, logged));
            return logged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose()
    {
    }

    private void Add(LogLevel level, string message)
    {
        lock (_waiters)
        {
            _entries.Enqueue((level, message));
            _waiters.RemoveAll(waiter =>
            {
                if (!message.Contains(waiter.Fragment, StringComparison.Ordinal))
                    return false;
                waiter.Logged.TrySetResult();
                return true;
            });
        }
    }

    /// <summary>
    /// The host forwards Serilog events here, and Serilog's own formatter quotes string
    /// values (<c>"ffmpeg"</c>). Render the original template the way
    /// Microsoft.Extensions.Logging does, so messages read the same as they did before Serilog.
    /// </summary>
    private static string Render<TState>(
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values)
            return formatter(state, exception);

        var properties = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (properties.GetValueOrDefault(OriginalFormatKey) is not string template)
            return formatter(state, exception);

        var message = new StringBuilder();
        foreach (MessageTemplateToken token in TemplateParser.Parse(template).Tokens)
        {
            if (token is PropertyToken property)
                message.Append(Format(properties.GetValueOrDefault(property.PropertyName), property.Format));
            else
                message.Append(((TextToken)token).Text);
        }

        return message.ToString();
    }

    // Mirrors Microsoft.Extensions.Logging's LogValuesFormatter: invariant culture,
    // "(null)" for nulls, and comma-joined sequences. Serilog forwards scalars as CLR
    // values but keeps sequences as SequenceValue. ListLoggerProviderTests pins this.
    private static string Format(object? value, string? format) => value switch
    {
        null => "(null)",
        string text => text,
        ScalarValue scalar => Format(scalar.Value, format),
        SequenceValue sequence => string.Join(", ", sequence.Elements.Select(item => Format(item, format))),
        IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
        IEnumerable sequence => string.Join(", ", sequence.Cast<object?>().Select(item => Format(item, format))),
        _ => value.ToString() ?? string.Empty,
    };

    private sealed class ListLogger(ListLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Add(logLevel, Render(state, exception, formatter));
    }
}
