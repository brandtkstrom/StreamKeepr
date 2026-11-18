using Microsoft.Extensions.Logging.Abstractions;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Storage;

public sealed class ViewerSamplesTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Append_writes_one_versioned_line_per_sample_in_seconds_from_the_session_start()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, Ct);

        DateTimeOffset start = SessionStoreTestContext.StartedAt;
        await context.Store.AppendViewerSampleAsync(context.Session, start.AddSeconds(123.44), 1658, Ct);
        await context.Store.AppendViewerSampleAsync(context.Session, start.AddSeconds(-5), 1600, Ct);

        Assert.Equal(
            "{\"v\":1,\"at\":123.4,\"count\":1658}\n{\"v\":1,\"at\":0,\"count\":1600}\n",
            await File.ReadAllTextAsync(ViewersPath(context)));
    }

    [Fact]
    public async Task Viewer_samples_survive_finalize_and_stay_out_of_the_manifest()
    {
        using var context = new SessionStoreTestContext();
        context.WritePart(1, PlaylistTestData.Segment("p001_00001.m4s", "2026-11-10T16:35:00Z", 1.5));
        context.WriteSegment(1, 1);
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        await context.Store.AppendViewerSampleAsync(
            context.Session, SessionStoreTestContext.StartedAt.AddSeconds(60), 42, Ct);

        await context.Store.FinalizeAsync(context.Session, Ct);

        Assert.Equal(
            [new ViewerSample(60, 42)],
            ViewerSamples.Read(context.Directory, NullLogger.Instance));
        string manifest = await File.ReadAllTextAsync(context.ManifestPath);
        Assert.DoesNotContain("viewer", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"count\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_after_the_session_folder_is_removed_fails_without_recreating_it()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        Directory.Delete(context.Directory, recursive: true);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            context.Store.AppendViewerSampleAsync(
                context.Session, SessionStoreTestContext.StartedAt, 1, Ct));

        Assert.False(Directory.Exists(context.Directory));
    }

    [Fact]
    public async Task A_partial_last_line_is_terminated_so_the_next_sample_survives()
    {
        using var context = new SessionStoreTestContext();
        await context.Store.OpenAsync(context.Session, context.Live, Ct);
        await File.WriteAllTextAsync(ViewersPath(context), "{\"v\":1,\"at\":0,\"count\":5}\n{\"v\":1,\"at\":6");

        await context.Store.AppendViewerSampleAsync(
            context.Session, SessionStoreTestContext.StartedAt.AddSeconds(120), 7, Ct);

        Assert.Equal(
            [new ViewerSample(0, 5), new ViewerSample(120, 7)],
            ViewerSamples.Read(context.Directory, NullLogger.Instance));
    }

    [Fact]
    public async Task Read_skips_malformed_lines_and_warns_once()
    {
        using var context = new SessionStoreTestContext();
        Directory.CreateDirectory(context.Directory);
        await File.WriteAllLinesAsync(ViewersPath(context),
        [
            """{"v":1,"at":0,"count":10}""",
            "not json",
            """{"v":2,"at":30,"count":11}""",
            """{"v":1,"at":60}""",
            """{"v":1,"at":-1,"count":12}""",
            """{"v":1,"at":90,"count":-3}""",
            "",
            """{"v":1,"at":120,"count":13}""",
            """{"v":1,"at":150,"count""",
        ]);
        var logs = new ListLoggerProvider();

        IReadOnlyList<ViewerSample> samples = ViewerSamples.Read(context.Directory, logs.CreateLogger("test"));

        Assert.Equal([new ViewerSample(0, 10), new ViewerSample(120, 13)], samples);
        string warning = Assert.Single(logs.Messages);
        Assert.Contains("Skipped 6 malformed viewer sample", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_without_a_viewer_log_returns_no_samples()
    {
        using var context = new SessionStoreTestContext();

        Assert.Empty(ViewerSamples.Read(context.Directory, NullLogger.Instance));
    }

    [Fact]
    public async Task Read_returns_at_most_the_sample_cap()
    {
        using var context = new SessionStoreTestContext();
        Directory.CreateDirectory(context.Directory);
        await File.WriteAllLinesAsync(
            ViewersPath(context),
            Enumerable.Range(0, ViewerSamples.MaxSamples + 5)
                .Select(i => $$"""{"v":1,"at":{{i * 60}},"count":{{i}}}"""));

        IReadOnlyList<ViewerSample> samples = ViewerSamples.Read(context.Directory, NullLogger.Instance);

        Assert.Equal(50_000, samples.Count);
        Assert.Equal(new ViewerSample(49_999 * 60, 49_999), samples[^1]);
    }

    private static string ViewersPath(SessionStoreTestContext context) =>
        Path.Combine(context.Directory, ViewerSamples.FileName);
}
