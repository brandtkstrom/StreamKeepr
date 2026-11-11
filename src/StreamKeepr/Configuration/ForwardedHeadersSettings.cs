namespace StreamKeepr.Configuration;

/// <summary>
/// The reverse proxies whose <c>X-Forwarded-*</c> headers are trusted. Both lists
/// default to empty, which means no client's forwarded headers are accepted.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "Hosting:ForwardedHeaders";

    public List<string> KnownProxies
    {
        get;
        set => field = value ?? [];
    } = [];

    public List<string> KnownNetworks
    {
        get;
        set => field = value ?? [];
    } = [];

    public bool IsConfigured => KnownProxies.Count > 0 || KnownNetworks.Count > 0;
}
