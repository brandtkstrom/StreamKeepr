using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Tests.Hosting;

public sealed class ExportDownloadEndpointTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sk-export-{Guid.NewGuid():N}.mp4");
    private readonly FakeExports _exports = new();

    public ExportDownloadEndpointTests() => File.WriteAllBytes(_file, Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());

    [Fact]
    public async Task A_succeeded_export_downloads_as_an_mp4_attachment_with_ranges()
    {
        _exports.Jobs["e1"] = new ExportJob("e1", "r1", ExportState.Succeeded, 1, "ann_20261110-1800_Title.mp4", null);
        _exports.Paths["e1"] = _file;
        using var factory = Factory(signedIn: true);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/exports/e1/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("ann_20261110-1800_Title.mp4", response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(1000, (await response.Content.ReadAsByteArrayAsync()).Length);

        using var range = new HttpRequestMessage(HttpMethod.Get, "/api/exports/e1/download");
        range.Headers.Range = new RangeHeaderValue(10, 19);
        using HttpResponseMessage partial = await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(Enumerable.Range(10, 10).Select(i => (byte)i), await partial.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Unknown_or_cleaned_up_is_404_and_unfinished_is_409()
    {
        _exports.Jobs["running"] = new ExportJob("running", "r1", ExportState.Running, 0.4, null, null);
        _exports.Jobs["cleaned"] = new ExportJob("cleaned", "r2", ExportState.Succeeded, 1, "x.mp4", null);
        using var factory = Factory(signedIn: true);
        using HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/exports/nope/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/exports/cleaned/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/exports/running/download")).StatusCode);
    }

    [Fact]
    public async Task Signed_out_is_401_without_a_login_redirect()
    {
        _exports.Jobs["e1"] = new ExportJob("e1", "r1", ExportState.Succeeded, 1, "x.mp4", null);
        _exports.Paths["e1"] = _file;
        using var factory = Factory(signedIn: false);
        using HttpClient client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage response = await client.GetAsync("/api/exports/e1/download");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public void Dispose() => File.Delete(_file);

    private StreamKeeprAppFactory Factory(bool signedIn) =>
        new(configureTestServices: services =>
        {
            services.RemoveAll<IExportService>();
            services.AddSingleton<IExportService>(_exports);
            if (signedIn)
            {
                // Authenticate every request as the admin; the cookie scheme still handles challenges.
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AdminHandler>(AdminHandler.SchemeName, null);
                services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
                    StreamKeepr.Infrastructure.Twitch.AdminAuth.CookieScheme,
                    options => options.ForwardAuthenticate = AdminHandler.SchemeName);
            }
        });

    private sealed class AdminHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestAdmin";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "mock_admin")], SchemeName)),
                SchemeName)));
    }

    private sealed class FakeExports : IExportService
    {
        public Dictionary<string, ExportJob> Jobs { get; } = new();

        public Dictionary<string, string> Paths { get; } = new();

        public event Action? Changed { add { } remove { } }

        public ExportJob Start(string recordingId, ClaimsPrincipal user, CancellationToken ct) =>
            throw new NotSupportedException();

        public ExportJob? Find(string exportId) => Jobs.GetValueOrDefault(exportId);

        public ExportJob? FindByRecording(string recordingId) =>
            Jobs.Values.LastOrDefault(job => job.RecordingId == recordingId);

        public string? FilePath(string exportId) =>
            Jobs.GetValueOrDefault(exportId)?.State == ExportState.Succeeded ? Paths.GetValueOrDefault(exportId) : null;
    }
}
