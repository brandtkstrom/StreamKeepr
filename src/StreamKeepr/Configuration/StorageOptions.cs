using System.ComponentModel.DataAnnotations;

namespace StreamKeepr.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required] public string RecordingsRoot { get; set => field = value?.Trim() ?? ""; } = "/recordings";

    [Range(1, 1000)] public int MinFreeGb { get; set; } = 10;

    /// <summary>The record store's file name in its directory, unless configured otherwise.</summary>
    public const string DatabaseFileName = "streamkeepr.db";

    /// <summary>
    /// The record store's SQLite file (ADR-0014). It is independent of
    /// <see cref="RecordingsRoot"/>: the container keeps it on its own <c>/data</c> volume.
    /// </summary>
    [Required] public string DatabasePath { get; set => field = value?.Trim() ?? ""; } = "/data/" + DatabaseFileName;

    /// <summary>Startup preflight fails when the database volume has less free space than this.</summary>
    [Range(1, 1000)] public int DatabaseMinFreeGb { get; set; } = 1;
}
