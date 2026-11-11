using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

internal sealed class TestClientStateStore : IClientStateStore
{
    private readonly Dictionary<string, object?> _persisted = new(StringComparer.Ordinal);

    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object?> Persisted => _persisted;

    public string? ReadCookie(string name) => Cookies.GetValueOrDefault(name);

    public bool TryRestore<T>(string key, out T? value)
    {
        if (_persisted.TryGetValue(key, out var stored) && stored is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public void Persist<T>(string key, T value) => _persisted[key] = value;
}
