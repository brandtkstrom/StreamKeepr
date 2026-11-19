using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using StreamKeepr.Infrastructure.Telemetry;

namespace StreamKeepr.Tests.Telemetry;

public sealed class TelemetryPathsTests
{
    [Theory]
    [InlineData("/signin-twitch")]
    [InlineData("/signin-twitch?code=secret&state=secret")]
    [InlineData("/media/channel/id/index.m3u8")]
    [InlineData("/_blazor")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public void Excluded_paths_are_not_traced(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = new PathString(path.Split('?')[0]);

        Assert.False(TelemetryPaths.ShouldTrace(context));
        Assert.True(TelemetryPaths.DisableHttpMetrics(context.Request.Path));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/recordings/abc/chat")]
    [InlineData("/api/auth/twitch/login")]
    public void Other_paths_are_traced(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = new PathString(path);

        Assert.True(TelemetryPaths.ShouldTrace(context));
        Assert.False(TelemetryPaths.DisableHttpMetrics(context.Request.Path));
    }

    [Fact]
    public void Rewrite_url_path_uses_the_route_template()
    {
        using var activity = new Activity("test").Start();
        activity.SetTag("http.route", "/api/recordings/{id}/chat");
        var context = new DefaultHttpContext();

        TelemetryPaths.RewriteUrlPath(activity, context.Response);

        Assert.Equal("/api/recordings/{id}/chat", activity.GetTagItem("url.path"));
    }

    [Fact]
    public void Rewrite_url_path_falls_back_for_unrouted_requests()
    {
        using var activity = new Activity("test").Start();
        var context = new DefaultHttpContext();

        TelemetryPaths.RewriteUrlPath(activity, context.Response);

        Assert.Equal("/[unrouted]", activity.GetTagItem("url.path"));
    }
}
