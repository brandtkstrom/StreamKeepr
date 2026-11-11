using System.Net;
using IPNetwork = System.Net.IPNetwork;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Hosting;

/// <summary>
/// Applies <c>X-Forwarded-*</c> headers only from an explicitly configured reverse
/// proxy. The framework trusts loopback by default; that implicit trust is cleared
/// so an unconfigured app behaves as if it were directly exposed.
/// </summary>
public static class ForwardedHeadersPipeline
{
    public static IServiceCollection AddStreamKeeprForwardedHeaders(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersSettings>()
            .BindConfiguration(ForwardedHeadersSettings.SectionName)
            .Validate(
                settings => settings.KnownProxies.All(proxy => IPAddress.TryParse(proxy, out _)),
                $"{ForwardedHeadersSettings.SectionName}:{nameof(ForwardedHeadersSettings.KnownProxies)} " +
                "contains an invalid IP address.")
            .Validate(
                settings => settings.KnownNetworks.All(network => IPNetwork.TryParse(network, out _)),
                $"{ForwardedHeadersSettings.SectionName}:{nameof(ForwardedHeadersSettings.KnownNetworks)} " +
                "contains an invalid network.")
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Adds the forwarded-header middleware only when at least one proxy or network
    /// is configured. Running it with the framework defaults would trust loopback.
    /// </summary>
    public static IApplicationBuilder UseStreamKeeprForwardedHeaders(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices
            .GetRequiredService<IOptions<ForwardedHeadersSettings>>()
            .Value;

        if (!settings.IsConfigured)
            return app;

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedHost
                | ForwardedHeaders.XForwardedProto,
        };

        // Remove the framework's loopback defaults before adding explicit trust.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in settings.KnownProxies)
            options.KnownProxies.Add(IPAddress.Parse(proxy));

        foreach (var network in settings.KnownNetworks)
            options.KnownIPNetworks.Add(IPNetwork.Parse(network));

        return app.UseForwardedHeaders(options);
    }
}
