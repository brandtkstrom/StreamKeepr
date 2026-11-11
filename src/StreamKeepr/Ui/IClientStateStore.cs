using Microsoft.AspNetCore.Components;

namespace StreamKeepr.Ui;

/// <summary>
/// Reads browser cookies during prerendering and carries the resulting state
/// across the prerender/circuit boundary through <see cref="PersistentComponentState"/>.
/// </summary>
public interface IClientStateStore
{
    string? ReadCookie(string name);

    bool TryRestore<T>(string key, out T? value);

    void Persist<T>(string key, T value);
}

public sealed class ClientStateStore(
    IHttpContextAccessor httpContextAccessor,
    PersistentComponentState componentState) : IClientStateStore
{
    public string? ReadCookie(string name) =>
        httpContextAccessor.HttpContext?.Request.Cookies[name];

    public bool TryRestore<T>(string key, out T? value) =>
        componentState.TryTakeFromJson(key, out value);

    public void Persist<T>(string key, T value)
    {
        // The circuit scope has no HttpContext; only the prerender scope can
        // register state for hydration.
        if (httpContextAccessor.HttpContext is null)
            return;

        componentState.RegisterOnPersisting(() =>
        {
            componentState.PersistAsJson(key, value);
            return Task.CompletedTask;
        });
    }
}
