using Microsoft.Extensions.Options;

namespace StreamKeepr.Configuration;

/// <summary>
/// The app bar's outbound links (design system: TopAppBar). They live in configuration so a
/// fork or a self-hosted install can point them elsewhere without a code change.
/// </summary>
public sealed class LinksOptions
{
    public const string SectionName = "Links";

    /// <summary>The project's source repository, opened by the GitHub icon.</summary>
    public string GitHub { get; set; } = "";

    /// <summary>Twitch itself, opened by the Twitch icon.</summary>
    public string Twitch { get; set; } = "";

    /// <summary>True for an absolute <c>http</c> or <c>https</c> URL, the only kind the app bar links to.</summary>
    public static bool IsValidUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}

public static class LinksOptionsServiceCollectionExtensions
{
    /// <summary>Binds and validates <see cref="LinksOptions"/> from the <c>Links</c> section at startup.</summary>
    public static IServiceCollection AddLinksOptions(this IServiceCollection services)
    {
        services.AddOptions<LinksOptions>()
                .BindConfiguration(LinksOptions.SectionName)
                .Validate(options => LinksOptions.IsValidUrl(options.GitHub),
                          $"{LinksOptions.SectionName}:{nameof(LinksOptions.GitHub)} must be an absolute http(s) URL.")
                .Validate(options => LinksOptions.IsValidUrl(options.Twitch),
                          $"{LinksOptions.SectionName}:{nameof(LinksOptions.Twitch)} must be an absolute http(s) URL.")
                .ValidateOnStart();
        return services;
    }
}
