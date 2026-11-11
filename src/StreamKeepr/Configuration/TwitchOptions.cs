using System.ComponentModel.DataAnnotations;

namespace StreamKeepr.Configuration;

public sealed class TwitchOptions
{
    public const string SectionName = "Twitch";

    [Required] public string ClientId { get; set => field = value?.Trim() ?? ""; } = "";

    [Required] public string ClientSecret { get; set => field = value?.Trim() ?? ""; } = "";

    // Seed list only: copied into channels.json on first start; after that the Settings page owns the list.
    public List<string> Channels
    {
        get;
        set => field = value is null
            ? []
            : value
                .Select(TwitchLogin.Normalize)
                .Distinct(StringComparer.Ordinal)
                .ToList();
    } = [];

    [Range(10, 600)] public int PollIntervalSeconds { get; set; } = 30;

    [Range(60, 3600)] public int PollIntervalWithEventSubSeconds { get; set; } = 300;
}
