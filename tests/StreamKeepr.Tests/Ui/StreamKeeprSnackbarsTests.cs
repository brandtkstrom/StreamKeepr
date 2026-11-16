using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class StreamKeeprSnackbarsTests
{
    [Fact]
    public async Task One_snackbar_shows_at_a_time_and_the_next_waits_its_turn()
    {
        await using var context = CreateContext();
        var snackbar = context.Services.GetRequiredService<ISnackbar>();
        var provider = context.Render<MudSnackbarProvider>();

        await provider.InvokeAsync(() =>
        {
            snackbar.Show("First");
            snackbar.Show("Second");
        });

        provider.WaitForAssertion(() => Assert.Equal(["First"], Messages(provider)));

        await provider.InvokeAsync(() => snackbar.Remove(snackbar.ShownSnackbars.Single()));

        provider.WaitForAssertion(() => Assert.Equal(["Second"], Messages(provider)));
    }

    [Fact]
    public async Task Equal_messages_in_a_row_each_get_their_turn()
    {
        await using var context = CreateContext();
        var snackbar = context.Services.GetRequiredService<ISnackbar>();

        snackbar.Show("Channel paused");
        snackbar.Show("Channel paused");

        Assert.Single(snackbar.ShownSnackbars);
        snackbar.Remove(snackbar.ShownSnackbars.Single());
        Assert.Equal("Channel paused", snackbar.ShownSnackbars.Single().Message);
    }

    [Fact]
    public async Task A_snackbar_offers_its_action_and_no_close_icon()
    {
        await using var context = CreateContext();
        var snackbar = context.Services.GetRequiredService<ISnackbar>();
        var provider = context.Render<MudSnackbarProvider>();
        var undone = false;

        await provider.InvokeAsync(() => snackbar.Show("Recording deleted", "Undo", () =>
        {
            undone = true;
            return Task.CompletedTask;
        }));

        var action = provider.WaitForElement("button");
        Assert.Equal("Undo", action.TextContent.Trim());
        Assert.Single(provider.FindAll("button"));

        await provider.InvokeAsync(() => action.Click());

        Assert.True(undone);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_snackbar_hides_on_its_own_with_or_without_an_action(bool withAction)
    {
        // The app's defaults with a short visible time, so the test waits well under a second.
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices(options =>
        {
            StreamKeeprSnackbars.Configure(options.SnackbarConfiguration);
            options.SnackbarConfiguration.VisibleStateDuration = 100;
            options.SnackbarConfiguration.ShowTransitionDuration = 10;
            options.SnackbarConfiguration.HideTransitionDuration = 10;
        });
        var snackbar = context.Services.GetRequiredService<ISnackbar>();
        var provider = context.Render<MudSnackbarProvider>();

        await provider.InvokeAsync(() =>
        {
            if (withAction)
                snackbar.Show("Recording deleted", "Undo", () => Task.CompletedTask);
            else
                snackbar.Show("Recording deleted");
        });

        Assert.Single(snackbar.ShownSnackbars);
        provider.WaitForAssertion(() => Assert.Empty(snackbar.ShownSnackbars), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Without_an_action_the_snackbar_has_no_buttons()
    {
        await using var context = CreateContext();
        var snackbar = context.Services.GetRequiredService<ISnackbar>();
        var provider = context.Render<MudSnackbarProvider>();

        await provider.InvokeAsync(() => snackbar.Show("Playlist URL copied"));

        provider.WaitForAssertion(() => Assert.Equal(["Playlist URL copied"], Messages(provider)));
        Assert.Empty(provider.FindAll("button"));
    }

    [Theory]
    [InlineData("Undo", false)]
    [InlineData(null, true)]
    [InlineData(" ", true)]
    public async Task An_action_needs_both_a_label_and_a_callback(string? label, bool withCallback)
    {
        await using var context = CreateContext();
        var snackbar = context.Services.GetRequiredService<ISnackbar>();
        Func<Task>? callback = withCallback ? () => Task.CompletedTask : null;

        Assert.ThrowsAny<ArgumentException>(() => snackbar.Show("Done", label, callback));
        Assert.Empty(snackbar.ShownSnackbars);
    }

    [Fact]
    public async Task Blank_text_is_rejected()
    {
        await using var context = CreateContext();
        var snackbar = context.Services.GetRequiredService<ISnackbar>();

        Assert.ThrowsAny<ArgumentException>(() => snackbar.Show(" "));
    }

    [Fact]
    public void The_defaults_follow_the_feedback_card()
    {
        var configuration = new SnackbarConfiguration();

        StreamKeeprSnackbars.Configure(configuration);

        Assert.Equal(Defaults.Classes.Position.BottomLeft, configuration.PositionClass);
        Assert.Equal(1, configuration.MaxDisplayedSnackbars);
        Assert.False(configuration.ShowCloseIcon);
        Assert.Equal(4000, configuration.VisibleStateDuration);
        Assert.Equal(100, configuration.MaximumOpacity);
    }

    private static string[] Messages(IRenderedComponent<MudSnackbarProvider> provider) =>
        provider.FindAll("[role='alert']")
            .Select(element => element.TextContent.Trim())
            .Where(text => text.Length > 0)
            .Distinct()
            .ToArray();

    internal static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddStreamKeeprMudServices();
        return context;
    }
}
