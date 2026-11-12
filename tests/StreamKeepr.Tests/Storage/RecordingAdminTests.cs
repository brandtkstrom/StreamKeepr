using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Storage;

public sealed class RecordingAdminTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Delete_requires_an_authenticated_admin()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);
        await SeedCompleteAsync(context);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            admin.DeleteAsync(context.Session.Id, Anonymous, Ct));

        Assert.NotNull(context.Library.Find(context.Session.Id));
        Assert.True(Directory.Exists(context.Directory));
    }

    [Fact]
    public async Task Delete_removes_the_folder_library_entry_and_progress()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(
            context, out TestStorageMonitor storage, out TestProgress progress, out _);
        await SeedCompleteAsync(context);
        SemaphoreSlim gateBefore = context.Store.LockFor(context.Directory);
        int libraryChanges = 0;
        context.Library.Changed += () => libraryChanges++;
        await progress.SetAsync(context.Session.Id, 1, Ct);
        Assert.Equal(1, progress.Get(context.Session.Id));

        await admin.DeleteAsync(context.Session.Id, Admin, Ct);

        Assert.Null(context.Library.Find(context.Session.Id));
        Assert.False(Directory.Exists(context.Directory));
        Assert.Null(progress.Get(context.Session.Id));
        Assert.Contains(context.Session.Id, progress.Cleared);
        Assert.Equal(1, storage.Refreshes);
        Assert.Equal(1, libraryChanges);
        Assert.NotSame(gateBefore, context.Store.LockFor(context.Directory));
    }

    [Fact]
    public async Task Delete_refuses_a_recording_that_is_still_being_captured()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 6));
        context.WriteSegment(1, 1, 10);
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        Assert.NotNull(context.Library.Find(context.Session.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.DeleteAsync(context.Session.Id, Admin, Ct));

        Assert.True(Directory.Exists(context.Directory));
    }

    [Fact]
    public async Task Delete_waits_for_the_session_lock()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);
        await SeedCompleteAsync(context);
        SemaphoreSlim gate = context.Store.LockFor(context.Directory);

        await gate.WaitAsync();
        Task delete;
        try
        {
            delete = admin.DeleteAsync(context.Session.Id, Admin, Ct);
            Assert.False(delete.IsCompleted);
        }
        finally
        {
            gate.Release();
        }

        await delete.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(context.Library.Find(context.Session.Id));
    }

    [Fact]
    public async Task Delete_refuses_while_an_export_lease_is_held()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(
            context, out _, out _, out ExportLeaseRegistry leases);
        await SeedCompleteAsync(context);
        using (leases.Acquire(context.Session.Id))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                admin.DeleteAsync(context.Session.Id, Admin, Ct));
            Assert.True(Directory.Exists(context.Directory));
        }
    }

    [Fact]
    public async Task Delete_reports_an_unknown_recording()
    {
        using var context = new SessionStoreTestContext();
        RecordingAdmin admin = CreateAdmin(context, out _, out _, out _);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            admin.DeleteAsync("20260101T000000Z_1", Admin, Ct));
    }

    [Fact]
    public void Lease_registry_makes_delete_and_export_start_mutually_exclusive()
    {
        var leases = new ExportLeaseRegistry();

        IDisposable? held = leases.TryBeginDelete("recording");
        Assert.NotNull(held);
        using (held)
            Assert.Throws<InvalidOperationException>(() => leases.Acquire("recording"));

        using (leases.Acquire("recording"))
            Assert.Null(leases.TryBeginDelete("recording"));
    }

    private static RecordingAdmin CreateAdmin(
        SessionStoreTestContext context,
        out TestStorageMonitor storage,
        out TestProgress progress,
        out ExportLeaseRegistry leases)
    {
        storage = new TestStorageMonitor();
        progress = new TestProgress();
        leases = new ExportLeaseRegistry();
        var options = Options.Create(new StorageOptions { RecordingsRoot = context.Root });
        return new RecordingAdmin(
            context.Library,
            context.Store,
            progress,
            leases,
            storage,
            options,
            NullLogger<RecordingAdmin>.Instance);
    }

    private static async Task SeedCompleteAsync(SessionStoreTestContext context)
    {
        context.WritePart(
            1,
            PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 60));
        context.WriteSegment(1, 1, 10);
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        context.Clock.Advance(TimeSpan.FromMinutes(1));
        await context.Store.FinalizeAsync(context.Session, Ct);
        Assert.NotNull(context.Library.Find(context.Session.Id));
    }

    private static ClaimsPrincipal Admin =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"));

    private static ClaimsPrincipal Anonymous => new(new ClaimsIdentity());

    private sealed class TestProgress : IProgressStore
    {
        private readonly Dictionary<string, double> _positions = new(StringComparer.Ordinal);

        public List<string> Cleared { get; } = [];

        public event Action? Changed;

        public double? Get(string recordingId) =>
            _positions.TryGetValue(recordingId, out double position) ? position : null;

        public Task SetAsync(string recordingId, double positionSec, CancellationToken ct)
        {
            _positions[recordingId] = positionSec;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task ClearAsync(string recordingId, CancellationToken ct)
        {
            _positions.Remove(recordingId);
            Cleared.Add(recordingId);
            Changed?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class TestStorageMonitor : IStorageMonitor
    {
        public int Refreshes { get; private set; }

        public StorageView Current => new(0, 0);

        public event Action? Changed;

        public void Refresh()
        {
            Refreshes++;
            Changed?.Invoke();
        }
    }
}
