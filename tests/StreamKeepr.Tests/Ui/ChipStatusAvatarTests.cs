using MudBlazor.Services;
using Bunit;
using Microsoft.AspNetCore.Components;
using StreamKeepr.Components.Primitives;

namespace StreamKeepr.Tests.Ui;

public sealed class ChipStatusAvatarTests
{
    [Theory]
    [InlineData(StatusChipKind.Rec, "rec", "Rec")]
    [InlineData(StatusChipKind.Reconnecting, "reconnecting", "Reconnecting")]
    [InlineData(StatusChipKind.Processing, "processing", "Processing")]
    [InlineData(StatusChipKind.Partial, "partial", "Partial")]
    [InlineData(StatusChipKind.Saved, "saved", "Saved")]
    [InlineData(StatusChipKind.Error, "error", "Error")]
    [InlineData(StatusChipKind.Healthy, "healthy", "Healthy")]
    public async Task Status_chip_exposes_its_state_and_label(
        StatusChipKind status,
        string expectedStatus,
        string expectedText)
    {
        await using var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        var cut = context.Render<StatusChip>(parameters => parameters
            .Add(component => component.Status, status));

        var chip = cut.Find("[data-status]");
        Assert.Equal(expectedStatus, chip.GetAttribute("data-status"));
        // Material Symbols render their ligature name as text; the label follows it.
        Assert.EndsWith(expectedText, chip.TextContent.Trim());
    }

    [Fact]
    public async Task A_status_chip_states_a_condition_and_is_never_a_control()
    {
        await using var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        var cut = context.Render<StatusChip>(parameters => parameters
            .Add(component => component.Status, StatusChipKind.Partial)
            .Add(component => component.Icon, "schedule"));

        var chip = cut.Find("[data-status='partial']");
        // MudChip leaves a passive chip at tabindex -1: never in the Tab order.
        Assert.NotEqual("0", chip.GetAttribute("tabindex") ?? "-1");
        Assert.Null(chip.GetAttribute("role"));
        Assert.Empty(cut.FindAll("button, a"));
        Assert.Equal("schedule", chip.GetAttribute("data-status-icon"));
        Assert.Equal("Partial", chip.TextContent.Trim());
    }

    [Fact]
    public async Task The_settings_healthy_chip_shows_a_check_circle()
    {
        await using var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        var cut = context.Render<StatusChip>(parameters => parameters
            .Add(component => component.Status, StatusChipKind.Healthy));

        Assert.Equal("check_circle", cut.Find("[data-status='healthy']").GetAttribute("data-status-icon"));
    }

    [Fact]
    public async Task An_unknown_icon_name_fails_fast()
    {
        await using var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        Assert.ThrowsAny<ArgumentException>(() => context.Render<StatusChip>(parameters => parameters
            .Add(component => component.Icon, "not_an_icon")));
    }

    [Fact]
    public async Task Rec_chip_has_the_pulse_dot_and_error_chip_has_a_warning_icon()
    {
        await using var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        var rec = context.Render<StatusChip>(parameters => parameters
            .Add(component => component.Status, StatusChipKind.Rec));
        var error = context.Render<StatusChip>(parameters => parameters
            .Add(component => component.Status, StatusChipKind.Error)
            .Add(component => component.Label, "Twitch session expired"));

        Assert.NotNull(rec.Find("[data-status='rec'] [data-status-dot]"));
        // MudChip draws its icon as SVG, so the chip names it in a hook (#176).
        Assert.Equal("warning", error.Find("[data-status='error']").GetAttribute("data-status-icon"));
        Assert.Contains("Twitch session expired", error.Markup);
    }

    [Fact]
    public async Task Avatar_uses_initials_and_announces_its_name()
    {
        await using var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        var cut = context.Render<Avatar>(parameters => parameters
            .Add(component => component.Name, "pixelwren")
            .Add(component => component.Tone, AvatarTone.Primary));

        var avatar = cut.Find("[role='img']");
        Assert.Equal("img", avatar.GetAttribute("role"));
        Assert.Equal("pixelwren", avatar.GetAttribute("aria-label"));
        Assert.Equal("pi", avatar.TextContent.Trim());
        Assert.Equal("primary", avatar.GetAttribute("data-tone"));
        Assert.Empty(cut.FindAll("[data-live-ring]"));
    }

    [Fact]
    public async Task Avatar_initials_use_the_first_two_words()
    {
        await using var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        var cut = context.Render<Avatar>(parameters => parameters
            .Add(component => component.Name, "lofi streams"));

        Assert.Equal("ls", cut.Find("[role='img']").TextContent.Trim());
    }

    [Fact]
    public async Task Avatar_can_use_an_image_and_show_the_recording_ring()
    {
        await using var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        var cut = context.Render<Avatar>(parameters => parameters
            .Add(component => component.Name, "pixelwren")
            .Add(component => component.ImageUrl, "https://example.test/avatar.png")
            .Add(component => component.Recording, true)
            .Add(component => component.Large, true));

        var avatar = cut.Find("[role='img']");
        Assert.Equal("large", avatar.GetAttribute("data-size"));
        Assert.Equal("https://example.test/avatar.png", avatar.QuerySelector("img")!.GetAttribute("src"));
        Assert.Equal("", avatar.QuerySelector("img")!.GetAttribute("alt"));
        Assert.NotNull(avatar.QuerySelector("[data-live-ring]"));
    }
}
