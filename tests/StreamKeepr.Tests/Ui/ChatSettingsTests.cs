using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Components.Features.Settings;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class ChatSettingsTests
{
    [Fact]
    public async Task Renders_a_labelled_slider_over_the_endpoint_limit_at_the_saved_offset()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        context.Services.GetRequiredService<UiPreferences>().SetChatSyncOffset(-4);

        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        Assert.Equal("Chat", cut.Find("[data-chat-settings] h2").TextContent.Trim());
        IElement range = cut.Find("[data-chat-sync-range]");
        Assert.Equal("range", range.GetAttribute("type"));
        Assert.Equal("-30", range.GetAttribute("min"));
        Assert.Equal("30", range.GetAttribute("max"));
        Assert.Equal("0.5", range.GetAttribute("step"));
        Assert.Equal("-4", range.GetAttribute("value"));
        Assert.Equal("4 seconds earlier", range.GetAttribute("aria-valuetext"));
        Assert.Equal("Chat sync", cut.Find($"label[for='{range.Id}']").TextContent.Trim());
        Assert.Equal("\u22124.0 s", cut.Find("[data-chat-sync-value]").TextContent.Trim());
        IElement reset = cut.Find("[data-chat-sync-reset]");
        Assert.Equal("Reset chat sync to 0", reset.GetAttribute("aria-label"));
        Assert.Equal("RestartAlt", reset.QuerySelector("svg")!.GetAttribute("data-icon"));
        string text = cut.Find("[data-chat-settings]").TextContent;
        Assert.Contains("Earlier", text, StringComparison.Ordinal);
        Assert.Contains("Later", text, StringComparison.Ordinal);
        Assert.Contains("Shifts chat on every recording", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0.0 s", "0 seconds")]
    [InlineData(0.5, "+0.5 s", "0.5 seconds later")]
    [InlineData(1, "+1.0 s", "1 second later")]
    [InlineData(-1, "\u22121.0 s", "1 second earlier")]
    [InlineData(-30, "\u221230.0 s", "30 seconds earlier")]
    public async Task Reads_out_and_names_the_offset(double offset, string shown, string spoken)
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        context.Services.GetRequiredService<UiPreferences>().SetChatSyncOffset(offset);

        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        Assert.Equal(shown, cut.Find("[data-chat-sync-value]").TextContent.Trim());
        Assert.Equal(spoken, cut.Find("[data-chat-sync-range]").GetAttribute("aria-valuetext"));
        Assert.Equal(offset == 0 ? "true" : "false", cut.Find("[data-chat-sync-value]").GetAttribute("data-zero"));
    }

    [Fact]
    public async Task Dragging_updates_the_readout_and_the_drag_end_saves_the_offset()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        cut.Find("[data-chat-sync-range]").Input("1.5");

        Assert.Equal("+1.5 s", cut.Find("[data-chat-sync-value]").TextContent.Trim());
        Assert.Equal(0, preferences.ChatSyncOffset);

        cut.Find("[data-chat-sync-range]").Change("1.5");

        Assert.Equal(1.5, preferences.ChatSyncOffset);
        Assert.Equal("1.5 seconds later", cut.Find("[data-chat-sync-range]").GetAttribute("aria-valuetext"));
    }

    [Fact]
    public async Task The_server_never_writes_the_thumb_back_while_it_moves()
    {
        // On a slow round trip, an echoed value lands after the next arrow press and undoes it,
        // so the rendered value stays put and the browser keeps the thumb (CI flake, PR #262).
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        foreach (string value in new[] { "0.5", "1", "1.5" })
        {
            cut.Find("[data-chat-sync-range]").Input(value);
            cut.Find("[data-chat-sync-range]").Change(value);
        }

        Assert.Equal("0", cut.Find("[data-chat-sync-range]").GetAttribute("value"));
        Assert.Equal("+1.5 s", cut.Find("[data-chat-sync-value]").TextContent.Trim());
    }

    [Fact]
    public async Task Reset_returns_the_offset_to_zero()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        preferences.SetChatSyncOffset(6);
        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        var before = cut.Find("[data-chat-sync-range]");
        Assert.Equal("6", before.GetAttribute("value"));

        cut.Find("[data-chat-sync-reset]").Click();

        Assert.Equal(0, preferences.ChatSyncOffset);
        Assert.Equal("0.0 s", cut.Find("[data-chat-sync-value]").TextContent.Trim());
        // Reset is the one move the server makes: a new slider, at 0.
        Assert.Equal("0", cut.Find("[data-chat-sync-range]").GetAttribute("value"));
    }

    [Fact]
    public async Task The_badge_text_row_reads_and_saves_the_preference()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        IElement toggle = cut.Find("[data-chat-badge-text]");
        Assert.Equal("checkbox", toggle.GetAttribute("type"));
        Assert.True(toggle.HasAttribute("checked"));
        Assert.Equal("Show badge text", toggle.ParentElement!.TextContent.Trim());
        IElement help = cut.Find("[data-chat-badge-text-help]");
        Assert.Equal(help.Id, toggle.GetAttribute("aria-describedby"));
        Assert.Contains("MOD, SUB, VIP", help.TextContent, StringComparison.Ordinal);

        toggle.Change(false);

        Assert.False(preferences.ChatShowBadgeText);
        Assert.False(cut.Find("[data-chat-badge-text]").HasAttribute("checked"));
        JSRuntimeInvocation write = Assert.Single(
            context.JSInterop.Invocations, call => call.Identifier == "skPrefs.writeUi");
        var state = Assert.IsType<UiPreferencesState>(Assert.Single(write.Arguments));
        Assert.False(state.ChatShowBadgeText);
    }

    [Fact]
    public async Task The_badge_text_row_shows_an_off_preference()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        context.Services.GetRequiredService<UiPreferences>().SetChatShowBadgeText(false);

        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        Assert.False(cut.Find("[data-chat-badge-text]").HasAttribute("checked"));
    }

    [Fact]
    public async Task The_font_size_row_runs_from_85_to_145_in_five_percent_steps()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);

        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        IElement range = cut.Find("[data-chat-font-scale]");
        Assert.Equal("range", range.GetAttribute("type"));
        Assert.Equal("85", range.GetAttribute("min"));
        Assert.Equal("145", range.GetAttribute("max"));
        Assert.Equal("5", range.GetAttribute("step"));
        Assert.Equal("100", range.GetAttribute("value"));
        Assert.Equal("100 percent", range.GetAttribute("aria-valuetext"));
        Assert.Equal("Chat font size", cut.Find($"label[for='{range.Id}']").TextContent.Trim());
        Assert.Equal("100%", cut.Find("[data-chat-font-value]").TextContent.Trim());
        string text = cut.Find("[data-chat-settings]").TextContent;
        Assert.Contains("Smaller", text, StringComparison.Ordinal);
        Assert.Contains("Larger", text, StringComparison.Ordinal);
        Assert.Contains("Scales chat replay text and emotes", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(85, "85%", "85 percent")]
    [InlineData(100, "100%", "100 percent")]
    [InlineData(145, "145%", "145 percent")]
    public async Task Reads_out_and_names_the_font_size(int scale, string shown, string spoken)
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        context.Services.GetRequiredService<UiPreferences>().SetChatFontScale(scale);

        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        Assert.Equal(shown, cut.Find("[data-chat-font-value]").TextContent.Trim());
        IElement range = cut.Find("[data-chat-font-scale]");
        Assert.Equal(scale.ToString(CultureInfo.InvariantCulture), range.GetAttribute("value"));
        Assert.Equal(spoken, range.GetAttribute("aria-valuetext"));
    }

    [Fact]
    public async Task Dragging_the_font_size_updates_the_readout_and_the_drag_end_saves()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        cut.Find("[data-chat-font-scale]").Input("120");

        Assert.Equal("120%", cut.Find("[data-chat-font-value]").TextContent.Trim());
        Assert.Equal(100, preferences.ChatFontScale);

        cut.Find("[data-chat-font-scale]").Change("120");

        Assert.Equal(120, preferences.ChatFontScale);
        Assert.Equal("120 percent", cut.Find("[data-chat-font-scale]").GetAttribute("aria-valuetext"));
        JSRuntimeInvocation write = Assert.Single(
            context.JSInterop.Invocations, call => call.Identifier == "skPrefs.writeUi");
        var state = Assert.IsType<UiPreferencesState>(Assert.Single(write.Arguments));
        Assert.Equal(120, state.ChatFontScale);
    }

    [Fact]
    public async Task The_server_never_writes_the_font_size_thumb_back_while_it_moves()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        foreach (string value in new[] { "105", "110", "115" })
        {
            cut.Find("[data-chat-font-scale]").Input(value);
            cut.Find("[data-chat-font-scale]").Change(value);
        }

        Assert.Equal("100", cut.Find("[data-chat-font-scale]").GetAttribute("value"));
        Assert.Equal("115%", cut.Find("[data-chat-font-value]").TextContent.Trim());
    }

    [Fact]
    public async Task The_font_weight_row_offers_the_design_weights()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        context.Services.GetRequiredService<UiPreferences>().SetChatFontWeight(500);

        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        IElement select = cut.Find("[data-chat-font-weight]");
        Assert.Equal("select", select.LocalName);
        Assert.Equal("Chat font weight", cut.Find($"label[for='{select.Id}']").TextContent.Trim());
        var options = select.QuerySelectorAll("option")
            .Select(option => (Value: option.GetAttribute("value"), Label: option.TextContent.Trim()))
            .ToArray();
        Assert.Equal(
            [("400", "Normal (400)"), ("500", "Medium (500)"), ("700", "Bold (700)")],
            options);
        Assert.Equal("500", select.QuerySelector("option[selected]")!.GetAttribute("value"));
        Assert.Contains("usernames stay bold", cut.Find("[data-chat-settings]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_font_weight_row_saves_and_persists()
    {
        await using BunitContext context = HomePlayerTests.CreateContext(out _, out _);
        UiPreferences preferences = context.Services.GetRequiredService<UiPreferences>();
        IRenderedComponent<ChatSettings> cut = context.Render<ChatSettings>();

        cut.Find("[data-chat-font-weight]").Change("700");

        Assert.Equal(700, preferences.ChatFontWeight);
        Assert.Equal("700", cut.Find("[data-chat-font-weight] option[selected]")!.GetAttribute("value"));
        JSRuntimeInvocation write = Assert.Single(
            context.JSInterop.Invocations, call => call.Identifier == "skPrefs.writeUi");
        var state = Assert.IsType<UiPreferencesState>(Assert.Single(write.Arguments));
        Assert.Equal(700, state.ChatFontWeight);
    }
}
