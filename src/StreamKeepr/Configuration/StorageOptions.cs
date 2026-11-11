using System.ComponentModel.DataAnnotations;

namespace StreamKeepr.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required] public string RecordingsRoot { get; set => field = value?.Trim() ?? ""; } = "/recordings";

    [Range(1, 1000)] public int MinFreeGb { get; set; } = 10;
}
