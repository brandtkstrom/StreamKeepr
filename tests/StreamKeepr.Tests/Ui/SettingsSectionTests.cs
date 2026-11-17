using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using StreamKeepr.Components.Features.Settings;

namespace StreamKeepr.Tests.Ui;

public sealed class SettingsSectionTests
{
    [Fact]
    public async Task On_the_page_a_section_is_labelled_by_its_h2()
    {
        await using var context = CreateContext();

        var cut = context.Render<SettingsSection>(Section);

        var heading = cut.Find("section > div > h2");
        Assert.Equal("Channels", heading.TextContent.Trim());
        Assert.Equal(heading.Id, cut.Find("section").GetAttribute("aria-labelledby"));
    }

    [Fact]
    public async Task Under_the_settings_sheet_cascade_a_section_heading_is_an_h3()
    {
        await using var context = CreateContext();

        var cut = context.Render<CascadingValue<bool>>(parameters => parameters
            .Add(value => value.Name, SettingsSheet.InSheetName)
            .Add(value => value.Value, true)
            .Add(value => value.IsFixed, true)
            .AddChildContent<SettingsSection>(Section));

        Assert.Empty(cut.FindAll("h2"));
        var heading = cut.Find("section > div > h3");
        Assert.Equal("Channels", heading.TextContent.Trim());
        Assert.Equal(heading.Id, cut.Find("section").GetAttribute("aria-labelledby"));
    }

    private static void Section(ComponentParameterCollectionBuilder<SettingsSection> parameters) => parameters
        .Add(section => section.HeadingId, "sk-settings-channels")
        .Add(section => section.Title, "Channels")
        .Add(section => section.Icon, MudBlazor.Icons.Material.Rounded.LiveTv);

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddStreamKeeprMudServices();
        return context;
    }
}
