using Microsoft.EntityFrameworkCore;
using StreamKeepr.Infrastructure.Records;

namespace StreamKeepr.Infrastructure.Storage;

/// <summary>One viewer-count sample: <see cref="At"/> seconds from the session start.</summary>
public sealed record ViewerSample(double At, int Count);

/// <summary>
/// The record store's <c>ViewerSamples</c> table (ADR-0014; session-store contract §7.2): one row
/// per observed sample, keyed to its session so deleting the session record cascades its samples.
/// The coordinator decides when to sample and <see cref="SessionStore"/> appends; failures
/// propagate to the caller, whose policy is to log and keep recording.
/// </summary>
public static class ViewerSamples
{
    /// <summary>The version of the <c>/api/recordings/{id}/viewers</c> body.</summary>
    public const int Version = 1;

    /// <summary>The most samples one read returns; later samples are left out.</summary>
    public const int MaxSamples = 50_000;

    /// <summary>Seconds from <paramref name="startedAt"/> to <paramref name="at"/>, to 0.1 s and never negative.</summary>
    public static double OffsetSeconds(DateTimeOffset startedAt, DateTimeOffset at) =>
        Math.Max(0, RoundSeconds((at - startedAt).TotalSeconds));

    /// <summary>Rounds to the 0.1 s precision of the viewer timeline.</summary>
    private static double RoundSeconds(double seconds) =>
        Math.Round(seconds, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Inserts one sample. A session without a record fails its foreign key, so a sample never
    /// outlives or precedes its session.
    /// </summary>
    public static Task AppendAsync(
        IDbContextFactory<RecordStoreDbContext> contexts,
        string sessionId,
        ViewerSample sample,
        CancellationToken ct) =>
        AppendRangeAsync(contexts, sessionId, [sample], ct);

    /// <summary>Inserts several samples in one transaction.</summary>
    public static async Task AppendRangeAsync(
        IDbContextFactory<RecordStoreDbContext> contexts,
        string sessionId,
        IEnumerable<ViewerSample> samples,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(samples);

        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        foreach (ViewerSample sample in samples)
        {
            if (!double.IsFinite(sample.At) || sample.At < 0)
                throw new ArgumentOutOfRangeException(nameof(samples), sample.At, "A sample offset must be finite and non-negative.");
            ArgumentOutOfRangeException.ThrowIfNegative(sample.Count, nameof(samples));

            db.ViewerSamples.Add(new ViewerSampleRow
            {
                SessionId = sessionId,
                OffsetSeconds = sample.At,
                Count = sample.Count,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The session's samples, oldest first (insertion order breaks ties), at most
    /// <see cref="MaxSamples"/>. A session without samples, or without a record, has none.
    /// </summary>
    public static async Task<IReadOnlyList<ViewerSample>> ReadAsync(
        IDbContextFactory<RecordStoreDbContext> contexts, string sessionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using RecordStoreDbContext db = await contexts.CreateDbContextAsync(ct);
        return await db.ViewerSamples.AsNoTracking()
            .Where(row => row.SessionId == sessionId)
            .OrderBy(row => row.OffsetSeconds)
            .ThenBy(row => row.Id)
            .Take(MaxSamples)
            .Select(row => new ViewerSample(row.OffsetSeconds, row.Count))
            .ToListAsync(ct);
    }
}
