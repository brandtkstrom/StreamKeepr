using Bunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using StreamKeepr.Components.Pages;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class DevComponentsTests
{
    [Fact]
    public void Development_renders_every_primitive()
    {
        using var context = CreateContext("Development");
        var cut = RenderPage(context);

        Assert.Contains("Design-system primitives", cut.Markup);
        Assert.NotEmpty(cut.FindAll(".sk-btn.filled"));
        Assert.NotEmpty(cut.FindAll(".sk-btn.tonal"));
        Assert.NotEmpty(cut.FindAll(".sk-btn.outlined"));
        Assert.NotEmpty(cut.FindAll(".sk-btn.text"));
        Assert.NotEmpty(cut.FindAll(".sk-btn.danger"));
        Assert.NotEmpty(cut.FindAll(".sk-icon-btn"));
        Assert.NotEmpty(cut.FindAll(".sk-chip[aria-pressed]"));
        Assert.NotEmpty(cut.FindAll(".sk-status.rec"));
        Assert.NotEmpty(cut.FindAll(".sk-status.partial"));
        Assert.NotEmpty(cut.FindAll(".sk-status.processing"));
        Assert.NotEmpty(cut.FindAll(".sk-status.saved"));
        Assert.NotEmpty(cut.FindAll(".sk-status.failed"));
        Assert.NotEmpty(cut.FindAll(".sk-avatar"));
        Assert.NotEmpty(cut.FindAll("section.sk-collapse"));
        Assert.NotEmpty(cut.FindAll("[role='switch']"));
        Assert.NotEmpty(cut.FindAll("[role='menu']"));
        Assert.NotEmpty(cut.FindAll("[role='tooltip']"));
        Assert.NotEmpty(cut.FindAll("dialog.sk-dialog"));
        Assert.NotEmpty(cut.FindAll(".sk-requires-twitch[inert]"));
    }

    [Fact]
    public void Outside_development_the_page_does_not_show_the_gallery()
    {
        using var context = CreateContext("Production");
        var cut = RenderPage(context);

        Assert.Contains("Page not found", cut.Markup);
        Assert.Empty(cut.FindAll(".sk-btn"));
        Assert.Empty(cut.FindAll("[role='menu']"));
    }

    private static BunitContext CreateContext(string environmentName)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.AddAuthorization().SetNotAuthorized();
        context.Services.AddLogging();
        context.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        context.Services.AddScoped<SnackbarService>();
        context.Services.AddSingleton<IWebHostEnvironment>(
            new TestWebHostEnvironment { EnvironmentName = environmentName });
        return context;
    }

    private static IRenderedComponent<DevComponents> RenderPage(BunitContext context) =>
        context.Render<DevComponents>();

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "StreamKeepr";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = "";

        public string EnvironmentName { get; set; } = "Development";

        public string ContentRootPath { get; set; } = "";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
