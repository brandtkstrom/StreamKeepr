using Microsoft.EntityFrameworkCore;
using StreamKeepr.Infrastructure.Records;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Records;

namespace StreamKeepr.Tests.Storage;

public sealed class ViewerSamplesTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Append_inserts_one_row_per_sample_in_seconds_from_the_session_start()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, Ct);

        DateTimeOffset start = SessionStoreTestContext.StartedAt;
        await context.Store.AppendViewerSampleAsync(context.Session, start.AddSeconds(123.44), 1658, Ct);
        await context.Store.AppendViewerSampleAsync(context.Session, start.AddSeconds(-5), 1600, Ct);

        await using RecordStoreDbContext db = context.RecordStore.Contexts.CreateDbContext();
        Assert.Equal(
            [(context.Session.Id, 123.4, 1658), (context.Session.Id, 0.0, 1600)],
            (await db.ViewerSamples.AsNoTracking().OrderBy(row => row.Id).ToListAsync(Ct))
                .Select(row => (row.SessionId, row.OffsetSeconds, row.Count)));
    }

    [Fact]
    public async Task Read_returns_samples_oldest_first()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        DateTimeOffset start = SessionStoreTestContext.StartedAt;
        await context.Store.AppendViewerSampleAsync(context.Session, start.AddSeconds(120), 7, Ct);
        await context.Store.AppendViewerSampleAsync(context.Session, start.AddSeconds(60), 6, Ct);
        await context.Store.AppendViewerSampleAsync(context.Session, start.AddSeconds(180), 8, Ct);

        Assert.Equal(
            [new ViewerSample(60, 6), new ViewerSample(120, 7), new ViewerSample(180, 8)],
            await ViewerSamples.ReadAsync(context.RecordStore.Contexts, context.Session.Id, Ct));
    }

    [Fact]
    public async Task Viewer_samples_survive_finalize_and_a_restart()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 1.5));
        context.WriteSegment(1, 1);
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        await context.Store.AppendViewerSampleAsync(
            context.Session, SessionStoreTestContext.StartedAt.AddSeconds(60), 42, Ct);

        await context.Store.FinalizeAsync(context.Session, Ct);
        await context.RecordStore.StartAsync();

        Assert.Equal(
            [new ViewerSample(60, 42)],
            await ViewerSamples.ReadAsync(context.RecordStore.Contexts, context.Session.Id, Ct));
        Assert.False(File.Exists(Path.Combine(context.Directory, "viewers.jsonl")));
    }

    [Fact]
    public async Task Append_for_a_session_without_a_record_fails_without_creating_its_folder()
    {
        using var context = new SessionStoreTestContext();

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            context.Store.AppendViewerSampleAsync(
                context.Session, SessionStoreTestContext.StartedAt, 1, Ct));

        Assert.False(Directory.Exists(context.Directory));
        Assert.Empty(await ViewerSamples.ReadAsync(context.RecordStore.Contexts, context.Session.Id, Ct));
    }

    [Fact]
    public async Task Deleting_the_session_record_cascades_its_samples()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        await context.Store.AppendViewerSampleAsync(
            context.Session, SessionStoreTestContext.StartedAt.AddSeconds(30), 9, Ct);

        await context.Store.DeleteRecordAsync(context.Session.Id, Ct);

        await using RecordStoreDbContext db = context.RecordStore.Contexts.CreateDbContext();
        Assert.Empty(await db.ViewerSamples.ToListAsync(Ct));
    }

    [Fact]
    public async Task Read_without_samples_returns_none()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, Ct);

        Assert.Empty(await ViewerSamples.ReadAsync(context.RecordStore.Contexts, context.Session.Id, Ct));
        Assert.Empty(await ViewerSamples.ReadAsync(context.RecordStore.Contexts, "missing", Ct));
    }

    [Fact]
    public async Task Read_returns_at_most_the_sample_cap_keeping_the_oldest()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        // Inserted newest first, so the cap must order before it takes.
        await ViewerSamples.AppendRangeAsync(
            context.RecordStore.Contexts,
            context.Session.Id,
            Enumerable.Range(0, ViewerSamples.MaxSamples + 5)
                .Reverse()
                .Select(i => new ViewerSample(i * 60, i)),
            Ct);

        IReadOnlyList<ViewerSample> samples =
            await ViewerSamples.ReadAsync(context.RecordStore.Contexts, context.Session.Id, Ct);

        Assert.Equal(50_000, samples.Count);
        Assert.Equal(new ViewerSample(0, 0), samples[0]);
        Assert.Equal(new ViewerSample(49_999 * 60, 49_999), samples[^1]);
    }

    [Fact]
    public async Task A_failed_write_throws_for_the_caller_to_log()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        context.Records.WritesFailing = true;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Store.AppendViewerSampleAsync(
                context.Session, SessionStoreTestContext.StartedAt, 1, Ct));
    }
}
