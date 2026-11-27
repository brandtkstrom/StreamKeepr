using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>One watched channel with its persisted pause and Twitch profile fields.</summary>
public sealed record ChannelEntry(
    string Login,
    bool Paused = false,
    string? UserId = null,
    string? DisplayName = null,
    string? AvatarUrl = null);

/// <summary>
/// The watched-channel list, held in the record store's <c>Channels</c> table (ADR-0014). Writes
/// are serialized and each is one transaction. <c>Twitch:Channels</c> seeds the list once: the
/// seed is recorded in the same transaction as its rows, so a list the user emptied stays empty.
/// </summary>
public sealed class ChannelStore(
    IOptions<TwitchOptions> twitch,
    IDbContextFactory<RecordStoreDbContext> records,
    RecordStoreState recordStore,
    ILogger<ChannelStore> logger)
{
    /// <summary>The <see cref="AppliedSeedRow"/> name for the <c>Twitch:Channels</c> seed.</summary>
    public const string SeedName = "channels";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlyList<ChannelEntry> _snapshot = Array.Empty<ChannelEntry>();
    private bool _loaded;

    public event Action? Changed;

    /// <summary>Raised only when a login or Twitch user ID is added or removed.</summary>
    public event Action? SubscriptionsChanged;

    public IReadOnlyList<ChannelEntry> Snapshot => _snapshot;

    /// <summary>
    /// Reads the stored list, first applying the configuration seed if it has never been applied.
    /// The seed is normalized and de-duplicated; an invalid seed or stored row throws.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        recordStore.EnsurePrepared();

        await _gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<ChannelEntry> stored = await ReadAsync(ct);
            if (await IsSeededAsync(ct))
            {
                _snapshot = stored;
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

            ChannelEntry[] seeded =
            [
                .. stored,
                .. seeds
                    .Distinct(StringComparer.Ordinal)
                    .Where(login => stored.All(entry => entry.Login != login))
                    .Select(login => new ChannelEntry(login)),
            ];

            await SaveAsync(seeded, ct, markSeeded: true);
            _loaded = true;
            logger.LogInformation(
                "Seeded the channel list with {Count} channels from {Section}.",
                seeded.Length - stored.Count,
                $"{TwitchOptions.SectionName}:{nameof(TwitchOptions.Channels)}");
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
            await SaveAsync(after, ct, markSeeded: false);
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
        await using RecordStoreDbContext db = await records.CreateDbContextAsync(ct);
        List<ChannelRow> rows = await db.Channels
            .AsNoTracking()
            .OrderBy(row => row.Position)
            .ToListAsync(ct);

        var entries = new ChannelEntry[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            ChannelRow row = rows[i];

            // Only this store writes the table, so a bad row means the file was edited or damaged.
            if (!IsValid(row.Login) || row.Login != Normalize(row.Login) || !IsValidUserId(row.UserId))
            {
                throw new InvalidDataException(
                    $"The record store's channel list contains an invalid row for login '{row.Login}'.");
            }

            entries[i] = new ChannelEntry(row.Login, row.Paused, row.UserId, row.DisplayName, row.AvatarUrl);
        }

        return Array.AsReadOnly(entries);
    }

    private async Task<bool> IsSeededAsync(CancellationToken ct)
    {
        await using RecordStoreDbContext db = await records.CreateDbContextAsync(ct);
        return await db.AppliedSeeds.AnyAsync(seed => seed.Name == SeedName, ct);
    }

    /// <summary>
    /// Writes the whole list in one transaction: rows not in it are deleted, the rest are
    /// inserted or updated, and each row's position is its index so the order round-trips.
    /// <paramref name="markSeeded"/> records the seed in that same transaction.
    /// </summary>
    private async Task SaveAsync(IReadOnlyList<ChannelEntry> list, CancellationToken ct, bool markSeeded)
    {
        await using RecordStoreDbContext db = await records.CreateDbContextAsync(ct);
        if (markSeeded)
            db.AppliedSeeds.Add(new AppliedSeedRow { Name = SeedName });

        Dictionary<string, ChannelRow> rows = await db.Channels
            .ToDictionaryAsync(row => row.Login, StringComparer.Ordinal, ct);

        var kept = new HashSet<string>(list.Select(entry => entry.Login), StringComparer.Ordinal);
        db.Channels.RemoveRange(rows.Values.Where(row => !kept.Contains(row.Login)));

        for (int position = 0; position < list.Count; position++)
        {
            ChannelEntry entry = list[position];
            if (!rows.TryGetValue(entry.Login, out ChannelRow? row))
            {
                row = new ChannelRow { Login = entry.Login };
                db.Channels.Add(row);
            }

            row.Position = position;
            row.Paused = entry.Paused;
            row.UserId = entry.UserId;
            row.DisplayName = entry.DisplayName;
            row.AvatarUrl = entry.AvatarUrl;
        }

        await db.SaveChangesAsync(ct);
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
