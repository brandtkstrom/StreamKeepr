using Serilog;

namespace StreamKeepr.Hosting;

/// <summary>
/// One Serilog completion event per HTTP request, leveled by <see cref="RequestLogPolicy"/>.
/// </summary>
internal static class RequestLoggingPipeline
{
    public const string MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms [{RequestId}]";

    /// <summary>
    /// Adds the request-logging middleware. Its place in the pipeline is fixed by
    /// <c>docs/hosting-contract.md</c> §2.
    /// </summary>
    public static IApplicationBuilder UseStreamKeeprRequestLogging(this IApplicationBuilder app)
    {
        // The host's DI logger, never the static Log.Logger: Testing and Mock hosts leave the
        // process-wide logger untouched, so request events must not depend on it.
        Serilog.ILogger logger = app.ApplicationServices.GetRequiredService<Serilog.ILogger>();

        return app.UseSerilogRequestLogging(options =>
        {
            options.Logger = logger;
            options.MessageTemplate = MessageTemplate;
            options.GetLevel = RequestLogPolicy.GetLevel;
            // Security control, not a default to rely on: /signin-twitch carries the OAuth
            // code and state in its query string. Never log the query.
            options.IncludeQueryInRequestPath = false;
            options.EnrichDiagnosticContext = static (diagnostics, context) =>
                diagnostics.Set("RequestId", context.TraceIdentifier);
        });
    }
}
