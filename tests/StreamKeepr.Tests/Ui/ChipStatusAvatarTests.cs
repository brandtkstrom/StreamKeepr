using Bunit;
using Microsoft.AspNetCore.Components;
using StreamKeepr.Components.Primitives;

namespace StreamKeepr.Tests.Ui;

public sealed class ChipStatusAvatarTests
{
    [Fact]
    public void Chip_exposes_selection_with_aria_pressed()
    {
        using var context = new BunitContext();
        var cut = context.Render<Chip>(parameters => parameters
            .Add(component => component.Selected, true)
            .AddChildContent("Recording"));

        var chip = cut.Find("button.sk-chip");
        Assert.Equal("true", chip.GetAttribute("aria-pressed"));
        Assert.Equal("check", cut.Find(".sk-chip .sk-icon").TextContent);
        Assert.Contains("Recording", chip.TextContent);
    }

    [Fact]
    public void Unselected_chip_has_no_check_icon_and_invokes_the_callback()
    {
        using var context = new BunitContext();
        var clicked = false;
        var cut = context.Render<Chip>(parameters => parameters
            .Add(component => component.Selected, false)
            .Add(component => component.OnClick,
                EventCallback.Factory.Create(this, () => clicked = true))
            .AddChildContent("Saved"));

        var chip = cut.Find("button.sk-chip");
        Assert.Equal("false", chip.GetAttribute("aria-pressed"));
        Assert.Empty(cut.FindAll(".sk-chip .sk-icon"));

        chip.Click();

        Assert.True(clicked);
    }

    [Theory]
    [InlineData(StatusChipKind.Rec, "rec", "Rec")]
    [InlineData(StatusChipKind.Reconnecting, "partial", "Reconnecting")]
    [InlineData(StatusChipKind.Processing, "processing", "Processing")]
    [InlineData(StatusChipKind.Partial, "partial", "Partial")]
    [InlineData(StatusChipKind.Saved, "saved", "Saved")]
    [InlineData(StatusChipKind.Error, "failed", "Error")]
    public void Status_chip_maps_state_to_class_and_label(
        StatusChipKind status,
        string expectedClass,
        string expectedText)
    {
        using var context = new BunitContext();
        var cut = context.Render<StatusChip>(parameters => parameters
            .Add(component => component.Status, status));

        var chip = cut.Find(".sk-status");
        Assert.Contains(expectedClass, chip.ClassList);
        // Material Symbols render their ligature name as text; the label follows it.
        Assert.EndsWith(expectedText, chip.TextContent.Trim());
    }

    [Fact]
    public void Rec_chip_has_the_pulse_dot_and_error_chip_has_a_warning_icon()
    {
        using var context = new BunitContext();
        var rec = context.Render<StatusChip>(parameters => parameters
            .Add(component => component.Status, StatusChipKind.Rec));
        var error = context.Render<StatusChip>(parameters => parameters
            .Add(component => component.Status, StatusChipKind.Error)
            .Add(component => component.Label, "Twitch session expired"));

        Assert.NotNull(rec.Find(".sk-status.rec .dot"));
        Assert.Equal("warning", error.Find(".sk-status.failed .sk-icon").TextContent);
        Assert.Contains("Twitch session expired", error.Markup);
    }

    [Fact]
    public void Avatar_uses_initials_and_announces_its_name()
    {
        using var context = new BunitContext();
        var cut = context.Render<Avatar>(parameters => parameters
            .Add(component => component.Name, "pixelwren")
            .Add(component => component.Tone, AvatarTone.Primary));

        var avatar = cut.Find(".sk-avatar");
        Assert.Equal("img", avatar.GetAttribute("role"));
        Assert.Equal("pixelwren", avatar.GetAttribute("aria-label"));
        Assert.Equal("pi", avatar.TextContent.Trim());
        Assert.Contains("c1", avatar.ClassList);
        Assert.Empty(cut.FindAll(".sk-live-ring"));
    }

    [Fact]
    public void Avatar_initials_use_the_first_two_words()
    {
        using var context = new BunitContext();
        var cut = context.Render<Avatar>(parameters => parameters
            .Add(component => component.Name, "lofi streams"));

        Assert.Equal("ls", cut.Find(".sk-avatar").TextContent.Trim());
    }

    [Fact]
    public void Avatar_can_use_an_image_and_show_the_recording_ring()
    {
        using var context = new BunitContext();
        var cut = context.Render<Avatar>(parameters => parameters
            .Add(component => component.Name, "pixelwren")
            .Add(component => component.ImageUrl, "https://example.test/avatar.png")
            .Add(component => component.Recording, true)
            .Add(component => component.Large, true));

        var avatar = cut.Find(".sk-avatar");
        Assert.Contains("lg", avatar.ClassList);
        Assert.Equal("https://example.test/avatar.png", cut.Find(".sk-avatar img").GetAttribute("src"));
        Assert.Equal("", cut.Find(".sk-avatar img").GetAttribute("alt"));
        Assert.NotNull(cut.Find(".sk-avatar .sk-live-ring"));
    }
}
