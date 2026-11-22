using Microsoft.Extensions.Options;

namespace StreamKeepr.Tests;

/// <summary>An options monitor over one mutable value, so a test can edit live configuration.</summary>
internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
