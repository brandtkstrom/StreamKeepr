using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Hosting;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>One watched channel with its persisted pause and Twitch profile fields.</summary>
public sealed record ChannelEntry(
    string Login,
    bool Paused = false,
    string? UserId = null,
    string? DisplayName = null,
    string? AvatarUrl = null);

/// <summary>
/// The persisted watched-channel list in <c>.streamkeepr/channels.json</c>. Writes are
/// serialized and atomic; a missing file seeds from <c>Twitch:Channels</c>, while a corrupt
/// file fails startup rather than silently resetting the list.
/// </summary>
public sealed class ChannelStore(
    IOptions<TwitchOptions> twitch,
    IOptions<StorageOptions> storage,
    ILogger<ChannelStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlyList<ChannelEntry> _snapshot = Array.Empty<ChannelEntry>();
    private bool _loaded;

    public event Action? Changed;

    /// <summary>Raised only when a login or Twitch user ID is added or removed.</summary>
    public event Action? SubscriptionsChanged;

    public IReadOnlyList<ChannelEntry> Snapshot => _snapshot;

    private string FilePath => AppDataPaths.ChannelsFile(storage.Value.RecordingsRoot);

    /// <summary>
    /// Reads the persisted list, or seeds it from configuration on first start. The seed is
    /// normalized and de-duplicated; a corrupt file throws and is left untouched.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(FilePath))
            {
                _snapshot = await ReadAsync(ct);
                _loaded = true;
                return;
            }

            string[] seeds = twitch.Value.Channels
                .Select(Normalize)
                .ToArray();

            string? invalidSeed = seeds.FirstOrDefault(login => !IsValid(login));
            if (invalidSeed is not null)
            {
                throw new InvalidDataException(
                    $"{TwitchOptions.SectionName}:{nameof(TwitchOptions.Channels)} contains " +
                    $"an invalid Twitch login '{invalidSeed}'.");
            }

            ChannelEntry[] seeded = seeds
                .Distinct(StringComparer.Ordinal)
                .Select(login => new ChannelEntry(login))
                .ToArray();

            await SaveAsync(seeded, ct);
            _loaded = true;
            logger.LogInformation(
                "Seeded the channel list with {Count} channels at {Path}.",
                seeded.Length,
                FilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds a channel if absent; adding an existing login is a no-op.</summary>
    public Task<ChannelEntry> AddAsync(string login, CancellationToken ct) =>
        AddAsync(new ChannelEntry(Normalize(login)), ct);

    /// <summary>Adds a channel with its resolved profile if absent.</summary>
    public async Task<ChannelEntry> AddAsync(ChannelEntry entry, CancellationToken ct)
    {
        string login = Normalize(entry.Login);
        if (!IsValid(login))
            throw new ArgumentException($"'{login}' is not a valid Twitch login.", nameof(login));
        if (!IsValidUserId(entry.UserId))
            throw new ArgumentException("A Twitch user ID must be numeric.", nameof(entry));

        entry = entry with { Login = login };
        IReadOnlyList<ChannelEntry> snapshot = await UpdateAsync(
            list => list.Any(existing => existing.Login == login)
                ? list
                : [.. list, entry],
            ct);

        return snapshot.First(existing => existing.Login == login);
    }

    public Task RemoveAsync(string login, CancellationToken ct)
    {
        login = Normalize(login);
        return UpdateAsync(
            list => list.Where(entry => entry.Login != login),
            ct);
    }

    public Task SetPausedAsync(string login, bool paused, CancellationToken ct)
    {
        login = Normalize(login);
        return UpdateAsync(
            list => list.Select(entry => entry.Login == login
                ? entry with { Paused = paused }
                : entry),
            ct);
    }

    /// <summary>Stores the Helix profile; a user-ID change restarts EventSub subscriptions.</summary>
    public Task SetProfileAsync(
        string login,
        string userId,
        string displayName,
        string? avatarUrl,
        CancellationToken ct)
    {
        login = Normalize(login);
        if (!IsValid(login))
            throw new ArgumentException($"'{login}' is not a valid Twitch login.", nameof(login));
        if (string.IsNullOrWhiteSpace(userId) || !IsValidUserId(userId))
            throw new ArgumentException("A Twitch user ID must be numeric.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return UpdateAsync(
            list => list.Select(entry => entry.Login == login
                ? entry with
                {
                    UserId = userId,
                    DisplayName = displayName,
                    AvatarUrl = avatarUrl,
                }
                : entry),
            ct);
    }

    private async Task<IReadOnlyList<ChannelEntry>> UpdateAsync(
        Func<IReadOnlyList<ChannelEntry>, IEnumerable<ChannelEntry>> change,
        CancellationToken ct)
    {
        IReadOnlyList<ChannelEntry> next;
        bool subscriptionsChanged;

        await _gate.WaitAsync(ct);
        try
        {
            if (!_loaded)
            {
                throw new InvalidOperationException(
                    "ChannelStore.LoadAsync must be called before modifying channels.");
            }

            IReadOnlyList<ChannelEntry> before = _snapshot;
            ChannelEntry[] after = change(before).ToArray();

            if (before.SequenceEqual(after))
                return before;

            subscriptionsChanged = !SubscriptionKeys(before).SetEquals(SubscriptionKeys(after));
            await SaveAsync(after, ct);
            next = _snapshot;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
        if (subscriptionsChanged)
            SubscriptionsChanged?.Invoke();

        return next;
    }

    private async Task<IReadOnlyList<ChannelEntry>> ReadAsync(CancellationToken ct)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(FilePath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw new InvalidDataException(
                $"Could not read the channel list at {FilePath}.", ex);
        }

        List<ChannelEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<ChannelEntry>>(text, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"The channel list at {FilePath} is not valid JSON.", ex);
        }

        if (entries is null)
            throw new InvalidDataException($"The channel list at {FilePath} is null.");

        var normalized = new List<ChannelEntry>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ChannelEntry? entry in entries)
        {
            if (entry is null)
                throw new InvalidDataException(
                    $"The channel list at {FilePath} contains a null entry.");

            if (!IsValidUserId(entry.UserId))
            {
                throw new InvalidDataException(
                    $"The channel list at {FilePath} contains an invalid user id.");
            }

            string login = Normalize(entry.Login);
            if (!IsValid(login))
            {
                throw new InvalidDataException(
                    $"The channel list at {FilePath} contains an invalid login '{entry.Login}'.");
            }

            if (!seen.Add(login))
            {
                throw new InvalidDataException(
                    $"The channel list at {FilePath} contains duplicate login '{login}'.");
            }

            normalized.Add(entry.Login == login ? entry : entry with { Login = login });
        }

        return Array.AsReadOnly(normalized.ToArray());
    }

    private async Task SaveAsync(IReadOnlyList<ChannelEntry> list, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);

        string temp = FilePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(list, Json), ct);
        File.Move(temp, FilePath, overwrite: true);

        _snapshot = Array.AsReadOnly(list.ToArray());
    }

    private static HashSet<(string Login, string? UserId)> SubscriptionKeys(
        IEnumerable<ChannelEntry> list) =>
        [.. list.Select(entry => (entry.Login, entry.UserId))];

    private static bool IsValidUserId(string? userId) =>
        userId is null
        || (userId.Length is >= 1 and <= 20 && userId.All(char.IsAsciiDigit));

    private static string Normalize(string? login) => TwitchLogin.Normalize(login);

    private static bool IsValid(string login) => TwitchLogin.IsValid(login);
}
