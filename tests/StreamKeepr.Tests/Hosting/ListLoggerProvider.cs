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
/// <see cref="Scopes"/> holds the properties of every <c>BeginScope</c> active on a direct
/// <see cref="ILogger"/> call, the innermost value winning. Through a Serilog pipeline with
/// <c>Enrich.FromLogContext</c>, scope values arrive in <see cref="Properties"/> instead.
/// </summary>
internal sealed record CapturedLogEntry(
    LogLevel Level,
    string Message,
    string? Template,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Scopes);

internal sealed class ListLoggerProvider : ILoggerProvider
{
    private const string OriginalFormatKey = "{OriginalFormat}";
    private static readonly MessageTemplateParser TemplateParser = new();

    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();
    private readonly List<(string Fragment, TaskCompletionSource Logged)> _waiters = [];
    private readonly AsyncLocal<ScopeFrame?> _scope = new();

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

    private CapturedLogEntry Capture<TState>(
        LogLevel level, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var properties = ReadProperties(state, out string? template);
        var scopes = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (ScopeFrame? frame = _scope.Value; frame is not null; frame = frame.Parent)
        {
            foreach ((string key, object? value) in frame.Properties)
                scopes.TryAdd(key, value);
        }

        return new CapturedLogEntry(
            level, Render(state, exception, formatter), template, properties, exception, scopes);
    }

    private IDisposable BeginScope(object state)
    {
        var frame = new ScopeFrame(this, _scope.Value, ReadProperties(state, out _));
        _scope.Value = frame;
        return frame;
    }

    private static Dictionary<string, object?> ReadProperties(object? state, out string? template)
    {
        template = null;
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is not IEnumerable<KeyValuePair<string, object?>> values)
            return properties;

        foreach ((string key, object? value) in values)
        {
            if (key == OriginalFormatKey)
                template = value as string;
            else
                properties[key] = Unwrap(value);
        }

        return properties;
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
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            owner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Add(owner.Capture(logLevel, state, exception, formatter));
    }

    /// <summary>One <c>BeginScope</c>; disposing it restores the enclosing scope on this flow.</summary>
    private sealed class ScopeFrame(
        ListLoggerProvider owner, ScopeFrame? parent, IReadOnlyDictionary<string, object?> properties)
        : IDisposable
    {
        public ScopeFrame? Parent => parent;

        public IReadOnlyDictionary<string, object?> Properties => properties;

        public void Dispose() => owner._scope.Value = parent;
    }
}
