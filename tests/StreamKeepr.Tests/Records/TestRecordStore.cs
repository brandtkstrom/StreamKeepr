using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Records;

/// <summary>
/// A real record store, configured exactly as the host configures one
/// (<see cref="RecordStoreRegistration.UseRecordStore"/>). Each <see cref="StartAsync"/> is
/// one process start: it runs the real startup step and returns that start's state, so a second
/// call is a restart against the same file.
/// </summary>
/// <remarks>
/// Without a recordings root it owns a temp directory and deletes it on dispose. Given one, it
/// keeps the database in a <c>.records</c> folder inside it (a dot name is never a channel), so
/// the caller's cleanup of that root removes it too.
/// </remarks>
internal sealed class TestRecordStore : IDisposable
{
    private readonly TempDirectory? _temp;

    public TestRecordStore(string? recordingsRoot = null)
    {
        if (recordingsRoot is null)
        {
            _temp = new TempDirectory();
            RecordingsRoot = Path.Combine(_temp.Path, "recordings");
            DataDirectory = Path.Combine(_temp.Path, "data");
        }
        else
        {
            RecordingsRoot = recordingsRoot;
            DataDirectory = DataDirectoryIn(recordingsRoot);
        }

        Directory.CreateDirectory(DataDirectory);
        Storage = Options.Create(new StorageOptions
        {
            RecordingsRoot = RecordingsRoot,
            DatabasePath = Path.Combine(DataDirectory, StorageOptions.DatabaseFileName),
        });

        var options = new DbContextOptionsBuilder<RecordStoreDbContext>();
        options.UseRecordStore(Storage.Value.DatabasePath);
        Contexts = new ContextFactory(options.Options);
    }

    public string DataDirectory { get; }

    /// <summary>Where tests keep the database for a recordings root; the app factory uses it too.</summary>
    public static string DataDirectoryIn(string recordingsRoot) => Path.Combine(recordingsRoot, ".records");

    public static string DatabasePathIn(string recordingsRoot) =>
        Path.Combine(DataDirectoryIn(recordingsRoot), StorageOptions.DatabaseFileName);

    public string DatabasePath => Storage.Value.DatabasePath;

    public string BackupDirectory => Path.Combine(DataDirectory, RecordStoreStartup.BackupDirectoryName);

    public string RecordingsRoot { get; }

    public IOptions<StorageOptions> Storage { get; }

    public IDbContextFactory<RecordStoreDbContext> Contexts { get; }

    public ListLoggerProvider Logs { get; } = new();

    public RecordStoreStartup CreateStartup(RecordStoreState state, TimeProvider? clock = null) =>
        new(
            Storage,
            Contexts,
            state,
            clock ?? TimeProvider.System,
            new CategoryLogger<RecordStoreStartup>(Logs.CreateLogger(nameof(RecordStoreStartup))));

    public async Task<RecordStoreState> StartAsync(TimeProvider? clock = null)
    {
        var state = new RecordStoreState();
        await CreateStartup(state, clock).StartAsync(CancellationToken.None);
        return state;
    }

    /// <summary>A store over this database for the given start, as the host would build it.</summary>
    public ChannelStore CreateChannelStore(RecordStoreState state, params string[] seeds) =>
        new(
            Options.Create(new TwitchOptions { Channels = [.. seeds] }),
            Contexts,
            state,
            NullLogger<ChannelStore>.Instance);

    /// <summary>Starts the store once and returns a loaded channel store over it.</summary>
    public async Task<ChannelStore> LoadChannelStoreAsync(params string[] seeds)
    {
        ChannelStore store = CreateChannelStore(await StartAsync(), seeds);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    /// <summary>
    /// A loaded channel store over a new record store kept inside <paramref name="recordingsRoot"/>,
    /// for tests that only need a working channel list and already clean up that root. Nothing
    /// else needs disposing: contexts are per call and connections are not pooled.
    /// </summary>
    public static Task<ChannelStore> LoadChannelStoreInAsync(string recordingsRoot, params string[] seeds) =>
        new TestRecordStore(recordingsRoot).LoadChannelStoreAsync(seeds);

    public void Dispose() => _temp?.Dispose();

    /// <summary>What <c>AddDbContextFactory</c> provides, without a service provider to own.</summary>
    private sealed class ContextFactory(DbContextOptions<RecordStoreDbContext> options)
        : IDbContextFactory<RecordStoreDbContext>
    {
        public RecordStoreDbContext CreateDbContext() => new(options);
    }

    private sealed class CategoryLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
