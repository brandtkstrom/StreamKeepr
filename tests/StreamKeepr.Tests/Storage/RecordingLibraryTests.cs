using Microsoft.Extensions.Logging.Abstractions;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Storage;

public sealed class RecordingLibraryTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Upsert_keeps_all_newest_first_and_find_returns_the_entry()
    {
        var library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);
        var older = Summary("older", StartedAt);
        var newer = Summary("newer", StartedAt.AddMinutes(5));

        library.Upsert(older);
        library.Upsert(newer);

        Assert.Equal([newer, older], library.All);
        Assert.Same(newer, library.Find("newer"));
        Assert.Same(older, library.Find("older"));
        Assert.Null(library.Find("missing"));
    }

    [Fact]
    public void Upsert_replaces_an_existing_entry_and_keeps_a_deterministic_order()
    {
        var library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);
        var first = Summary("b", StartedAt);
        var second = Summary("a", StartedAt);

        library.Upsert(first);
        library.Upsert(second);

        Assert.Equal(["a", "b"], library.All.Select(summary => summary.Id));
    }

    [Fact]
    public void Upsert_remove_and_mark_loaded_raise_changed()
    {
        var library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);
        int changed = 0;
        library.Changed += () => changed++;

        Assert.False(library.IsLoaded);

        library.Upsert(Summary("one", StartedAt));
        Assert.Equal(1, changed);

        library.Upsert(Summary("one", StartedAt));
        Assert.Equal(2, changed);

        library.Remove("missing");
        Assert.Equal(2, changed);

        library.Remove("one");
        Assert.Equal(3, changed);
        Assert.Empty(library.All);

        library.MarkLoaded();
        Assert.True(library.IsLoaded);
        Assert.Equal(4, changed);

        library.MarkLoaded();
        Assert.Equal(4, changed);
    }

    [Fact]
    public void Concurrent_upserts_notify_subscribers_and_keep_every_entry()
    {
        var library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);
        int changed = 0;
        library.Changed += () => Interlocked.Increment(ref changed);
        var summaries = Enumerable.Range(0, 50)
            .Select(index => Summary($"r{index}", StartedAt.AddSeconds(index)))
            .ToArray();

        Parallel.ForEach(summaries, library.Upsert);

        Assert.Equal(summaries.Length, Volatile.Read(ref changed));
        Assert.Equal(summaries.Length, library.All.Count);
    }

    [Fact]
    public void All_is_a_snapshot_that_later_upserts_do_not_mutate()
    {
        var library = new RecordingLibrary(NullLogger<RecordingLibrary>.Instance);
        library.Upsert(Summary("one", StartedAt));

        IReadOnlyList<RecordingSummary> snapshot = library.All;

        library.Upsert(Summary("two", StartedAt.AddMinutes(1)));

        Assert.Single(snapshot);
        Assert.Equal(2, library.All.Count);
    }

    private static RecordingSummary Summary(string id, DateTimeOffset startedAt) =>
        new(
            id,
            "streamer",
            $"Title {id}",
            "Just Chatting",
            startedAt,
            EndedAt: null,
            IsRecording: true,
            DurationSeconds: null,
            SizeBytes: 0,
            PartCount: 0,
            SegmentCount: 0,
            HasThumbnail: false,
            Chapters: []);
}
