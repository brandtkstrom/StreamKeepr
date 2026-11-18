using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace StreamKeepr.Tests.Hosting;

/// <summary>
/// One captured log call. <see cref="Message"/> is rendered the way Microsoft.Extensions.Logging
/// renders it; <see cref="Template"/> is the <c>{OriginalFormat}</c> template, or null when the
/// state has none. <see cref="Properties"/> holds the template's values with Serilog's
/// <see cref="ScalarValue"/> and <see cref="SequenceValue"/> wrappers unwrapped to CLR values.
/// </summary>
internal sealed record CapturedLogEntry(
    LogLevel Level,
    string Message,
    string? Template,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception);

internal sealed class ListLoggerProvider : ILoggerProvider
{
    private const string OriginalFormatKey = "{OriginalFormat}";
    private static readonly MessageTemplateParser TemplateParser = new();

    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();
    private readonly List<(string Fragment, TaskCompletionSource Logged)> _waiters = [];

    /// <summary>
    /// A snapshot. Host logging can run on another thread while a test enumerates, so callers
    /// must never observe the live collection.
    /// </summary>
    public IReadOnlyList<string> Messages => _entries.Select(entry => entry.Message).ToArray();

    /// <summary>A snapshot of every captured call; see <see cref="Messages"/> on snapshots.</summary>
    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

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

    private void Add(CapturedLogEntry entry)
    {
        lock (_waiters)
        {
            _entries.Enqueue(entry);
            _waiters.RemoveAll(waiter =>
            {
                if (!entry.Message.Contains(waiter.Fragment, StringComparison.Ordinal))
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

    private static CapturedLogEntry Capture<TState>(
        LogLevel level, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        string? template = null;
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach ((string key, object? value) in values)
            {
                if (key == OriginalFormatKey)
                    template = value as string;
                else
                    properties[key] = Unwrap(value);
            }
        }

        return new CapturedLogEntry(
            level, Render(state, exception, formatter), template, properties, exception);
    }

    private static object? Unwrap(object? value) => value switch
    {
        ScalarValue scalar => scalar.Value,
        SequenceValue sequence => sequence.Elements.Select(Unwrap).ToArray(),
        _ => value,
    };

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
            owner.Add(Capture(logLevel, state, exception, formatter));
    }
}
