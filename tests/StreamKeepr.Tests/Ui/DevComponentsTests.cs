using MudBlazor.Services;
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
    public async Task Development_renders_every_primitive()
    {
        await using var context = CreateContext("Development");
        var cut = RenderPage(context);

        Assert.Contains("Design-system primitives", cut.Markup);
        Assert.NotEmpty(cut.FindAll("button[data-variant='filled']"));
        Assert.NotEmpty(cut.FindAll("button[data-variant='tonal']"));
        Assert.NotEmpty(cut.FindAll("button[data-variant='outlined']"));
        Assert.NotEmpty(cut.FindAll("button[data-variant='text']"));
        Assert.NotEmpty(cut.FindAll("button[data-variant='danger']"));
        Assert.NotEmpty(cut.FindAll("button[aria-label='Stats'][aria-pressed='true']"));
        Assert.NotEmpty(cut.FindAll("button[aria-pressed]"));
        Assert.NotEmpty(cut.FindAll("[data-status='rec']"));
        Assert.NotEmpty(cut.FindAll("[data-status='partial']"));
        Assert.NotEmpty(cut.FindAll("[data-status='processing']"));
        Assert.NotEmpty(cut.FindAll("[data-status='saved']"));
        Assert.NotEmpty(cut.FindAll("[data-status='error']"));
        Assert.NotEmpty(cut.FindAll("[role='img'][data-tone]"));
        Assert.NotEmpty(cut.FindAll("section[data-collapsible]"));
        Assert.NotEmpty(cut.FindAll("[role='switch']"));
        Assert.NotEmpty(cut.FindAll("[role='tooltip']"));
        Assert.NotEmpty(cut.FindAll("[data-requires-twitch] [inert]"));
    }

    [Fact]
    public async Task Outside_development_the_page_does_not_show_the_gallery()
    {
        await using var context = CreateContext("Production");
        var cut = RenderPage(context);

        Assert.Contains("Page not found", cut.Markup);
        Assert.Empty(cut.FindAll("button[data-variant]"));
        Assert.Empty(cut.FindAll("[role='menu']"));
    }

    private static BunitContext CreateContext(string environmentName)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddStreamKeeprMudServices();
        context.AddAuthorization().SetNotAuthorized();
        context.Services.AddLogging();
        context.Services.AddSingleton<TimeProvider>(TimeProvider.System);
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
