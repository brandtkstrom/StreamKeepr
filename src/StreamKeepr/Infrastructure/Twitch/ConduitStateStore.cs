using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

namespace StreamKeepr.Infrastructure.Twitch;

/// <summary>
/// The conduit this instance created, and the client ID that owns it. Neither is a secret.
/// Subscription IDs are not stored: Twitch lists them by conduit.
/// </summary>
public sealed record ConduitState(
    [property: JsonPropertyName("conduit_id")] string ConduitId,
    [property: JsonPropertyName("client_id")] string ClientId);

/// <summary>Persists <see cref="ConduitState"/> so a restart reuses the conduit instead of leaking one.</summary>
public interface IConduitStateStore
{
    /// <summary>Returns null when nothing was saved or the file is unreadable.</summary>
    Task<ConduitState?> LoadAsync(CancellationToken ct);

    Task SaveAsync(ConduitState state, CancellationToken ct);

    Task ClearAsync(CancellationToken ct);
}

/// <summary>
/// Stores the conduit state in <c>.streamkeepr/eventsub-conduit.json</c>, owner-only and written
/// atomically. Callers are serialized by the single EventSub loop, so no lock is taken.
/// </summary>
public sealed class ConduitStateStore(
    IOptions<StorageOptions> storage,
    ILogger<ConduitStateStore> logger) : IConduitStateStore
{
    private readonly string _path = AppDataPaths.EventSubConduitFile(storage.Value.RecordingsRoot);

    public async Task<ConduitState?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            await using FileStream stream = File.OpenRead(_path);
            ConduitState? state = await JsonSerializer.DeserializeAsync(
                stream, TwitchJsonContext.Default.ConduitState, ct);
            if (state is { ConduitId: { Length: > 0 }, ClientId: { Length: > 0 } })
                return state;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The EventSub conduit state file is malformed.");
        }

        // Degrade to "no conduit": a new one is created and the lost one expires on Twitch's side.
        logger.LogWarning(
            "Ignoring unreadable EventSub conduit state at {Path}; a new conduit will be created.",
            _path);
        return null;
    }

    public Task SaveAsync(ConduitState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.ConduitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.ClientId);
        ct.ThrowIfCancellationRequested();

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(state, TwitchJsonContext.Default.ConduitState);
        OwnerOnlyFileSystem.EnsurePrivateDirectory(Path.GetDirectoryName(_path)!);
        OwnerOnlyFileSystem.WriteAtomically(_path, json);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(_path))
            File.Delete(_path);
        return Task.CompletedTask;
    }
}
