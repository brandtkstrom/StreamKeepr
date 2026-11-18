using Microsoft.AspNetCore.Http;
using Serilog.Events;
using StreamKeepr.Hosting;

namespace StreamKeepr.Tests.Hosting;

public sealed class RequestLogPolicyTests
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/media/ann/r1/index.m3u8")]
    [InlineData("/media/ann/r1/p001_00001.m4s")]
    [InlineData("/_blazor")]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("/api/recordings/r1/progress")]
    [InlineData("/api/recordings/r1/chat")]
    [InlineData("/app.css")]
    [InlineData("/js/player.js")]
    [InlineData("/favicon.ico")]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/fonts/MaterialSymbols.woff2")]
    public void Successful_quiet_path_requests_are_verbose(string path)
    {
        Assert.Equal(LogEventLevel.Verbose, RequestLogPolicy.GetLevel(path, 200, exception: null));
    }

    [Theory]
    [InlineData("/health/live", 304)]
    [InlineData("/media/ann/r1/thumb.jpg", 304)]
    [InlineData("/api/recordings/r1/progress", 204)]
    public void Non_error_statuses_on_quiet_paths_are_verbose(string path, int status)
    {
        Assert.Equal(LogEventLevel.Verbose, RequestLogPolicy.GetLevel(path, status, exception: null));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/channels")]
    [InlineData("/recordings/r1")]
    [InlineData("/signin-twitch")]
    [InlineData("/api/exports/e1/download")]
    public void Successful_page_and_api_requests_are_information(string path)
    {
        Assert.Equal(LogEventLevel.Information, RequestLogPolicy.GetLevel(path, 200, exception: null));
    }

    [Theory]
    [InlineData("/healthy")]
    [InlineData("/mediafile")]
    [InlineData("/api/recordingsx")]
    public void Quiet_prefixes_match_whole_path_segments_only(string path)
    {
        Assert.Equal(LogEventLevel.Information, RequestLogPolicy.GetLevel(path, 200, exception: null));
    }

    [Theory]
    [InlineData("/media/nope/nope/index.m3u8", 404)]
    [InlineData("/api/recordings/r1/progress", 400)]
    [InlineData("/health/live", 404)]
    [InlineData("/_framework/missing.js", 404)]
    [InlineData("/", 400)]
    [InlineData("/settings", 401)]
    [InlineData("/settings", 403)]
    [InlineData("/nope", 404)]
    [InlineData("/", 499)]
    public void Client_errors_are_warnings_on_every_path(string path, int status)
    {
        Assert.Equal(LogEventLevel.Warning, RequestLogPolicy.GetLevel(path, status, exception: null));
    }

    [Theory]
    [InlineData("/health/ready", 503)]
    [InlineData("/media/ann/r1/index.m3u8", 500)]
    [InlineData("/_blazor", 500)]
    [InlineData("/", 500)]
    [InlineData("/api/exports/e1/download", 502)]
    public void Server_errors_are_errors_on_every_path(string path, int status)
    {
        Assert.Equal(LogEventLevel.Error, RequestLogPolicy.GetLevel(path, status, exception: null));
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/")]
    public void An_unhandled_exception_is_an_error_whatever_the_status(string path)
    {
        Assert.Equal(
            LogEventLevel.Error,
            RequestLogPolicy.GetLevel(path, 200, new InvalidOperationException("boom")));
    }

    [Fact]
    public void The_middleware_callback_reads_path_and_status_from_the_context()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/media/nope/nope/index.m3u8";
        context.Response.StatusCode = 404;

        Assert.Equal(LogEventLevel.Warning, RequestLogPolicy.GetLevel(context, 1.5, exception: null));
    }
}
