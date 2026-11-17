using AngleSharp.Dom;
using Bunit;
using StreamKeepr.Components.Features.Home;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class RecordingCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_open_session_is_a_link_named_by_title_streamer_and_state()
    {
        await using BunitContext context = CreateContext();

        IRenderedComponent<RecordingCard> cut = Render(context, Recording(
            "r 1", RecordingState.Recording, startedAt: new(2026, 11, 11, 21, 4, 0, TimeSpan.Zero),
            durationSec: 3725, title: "Late-night speedrun practice", category: "Speedrunning"));

        IElement link = cut.Find("a");
        Assert.Equal("/r/r%201", link.GetAttribute("href"));
        // The same data-state words as the drawer rows.
        Assert.Equal("rec", link.GetAttribute("data-state"));
        Assert.Equal("Late-night speedrun practice, Nightjar TV, recording", AccessibleName(link));
        Assert.Null(link.GetAttribute("aria-label"));
        Assert.Equal(
            "Nightjar TV · Speedrunning · Started 9:04 PM",
            cut.Find($"#{link.GetAttribute("aria-describedby")}").TextContent);
        Assert.Equal("rec", cut.Find("[data-thumb-state] [data-status]").GetAttribute("data-status"));
        Assert.Equal("1:02:05", cut.Find("[data-duration]").TextContent);
        Assert.Equal("Nightjar TV · Speedrunning · Started 9:04 PM", cut.Find("[data-card-meta]").TextContent);
        Assert.Equal("100%", ProgressWidth(cut));
        Assert.Empty(cut.FindAll("[data-meta-state]"));
    }

    [Fact]
    public async Task A_reconnecting_session_carries_the_reconnecting_chip()
    {
        await using BunitContext context = CreateContext();

        IRenderedComponent<RecordingCard> cut = Render(context, Recording("r1", RecordingState.Reconnecting));

        Assert.Equal("reconnecting", cut.Find("[data-thumb-state] [data-status]").GetAttribute("data-status"));
        Assert.Equal("reconnecting", cut.Find("a").GetAttribute("data-state"));
        Assert.EndsWith(", reconnecting", AccessibleName(cut.Find("a")));
    }

    [Fact]
    public async Task A_saved_session_shows_its_day_and_resume_position_without_a_chip()
    {
        await using BunitContext context = CreateContext();

        IRenderedComponent<RecordingCard> cut = Render(context, Recording(
            "r1", RecordingState.Saved, startedAt: new(2026, 11, 10, 20, 30, 0, TimeSpan.Zero),
            durationSec: 4000, resumeSec: 1000));

        Assert.Equal("Nightjar TV · Yesterday, 8:30 PM", cut.Find("[data-card-meta]").TextContent);
        Assert.Equal("25%", ProgressWidth(cut));
        Assert.Empty(cut.FindAll("[data-status]"));
        Assert.EndsWith(", saved", AccessibleName(cut.Find("a")));
    }

    [Fact]
    public async Task A_partial_session_has_its_chip_under_the_meta_line_and_no_strip_unwatched()
    {
        await using BunitContext context = CreateContext();

        IRenderedComponent<RecordingCard> cut = Render(context, Recording("r1", RecordingState.Partial));

        Assert.Equal("partial", cut.Find("[data-meta-state] [data-status]").GetAttribute("data-status"));
        Assert.Empty(cut.FindAll("[data-thumb-state]"));
        Assert.Empty(cut.FindAll("[data-progress]"));
    }

    [Fact]
    public async Task The_size_and_thumbnail_follow_the_parameters()
    {
        await using BunitContext context = CreateContext();

        IRenderedComponent<RecordingCard> large = Render(
            context, Recording("r1", RecordingState.Saved, thumbnail: "/thumbs/r1.jpg"), RecordingCardSize.Large);
        IRenderedComponent<RecordingCard> plain = Render(context, Recording("r2", RecordingState.Saved));

        Assert.Equal("large", large.Find("a").GetAttribute("data-card-size"));
        Assert.Equal("/thumbs/r1.jpg", large.Find("img[loading='lazy']").GetAttribute("src"));
        Assert.Equal("default", plain.Find("a").GetAttribute("data-card-size"));
        Assert.Empty(plain.FindAll("[data-thumb] img"));
        // The avatar is the channel's initial on its colour, hidden because the name is the meta line's.
        IElement avatar = plain.Find("[data-channel-color]");
        Assert.Equal("N", avatar.TextContent.Trim());
        Assert.Equal("true", avatar.GetAttribute("aria-hidden"));
    }

    // The name from content: every text node outside an aria-hidden subtree, as a browser computes it here.
    internal static string AccessibleName(IElement link) =>
        string.Concat(link.Descendants<IText>()
            .Where(text => !text.Ancestors<IElement>().Any(element => element.GetAttribute("aria-hidden") == "true"))
            .Select(text => text.Data)).Trim();

    private static string? ProgressWidth(IRenderedComponent<RecordingCard> cut) =>
        cut.Find("[data-progress] i").GetAttribute("style")?.Replace("width:", "", StringComparison.Ordinal);

    private static IRenderedComponent<RecordingCard> Render(
        BunitContext context, RecordingView recording, RecordingCardSize size = RecordingCardSize.Default) =>
        context.Render<RecordingCard>(parameters => parameters
            .Add(card => card.Recording, recording)
            .Add(card => card.StreamerName, "Nightjar TV")
            .Add(card => card.Size, size)
            .Add(card => card.TimeZone, TimeZoneInfo.Utc)
            .Add(card => card.Now, Now));

    internal static RecordingView Recording(
        string id,
        RecordingState state,
        DateTimeOffset? startedAt = null,
        double durationSec = 600,
        double? resumeSec = null,
        string title = "Recording",
        string? category = "Game",
        string login = "nightjar_tv",
        string? thumbnail = null) =>
        new(id, login, title, category, startedAt ?? Now.AddHours(-1), null, durationSec, state,
            4, state == RecordingState.Partial ? 2 : 1, null, thumbnail, $"/media/{login}/{id}/index.m3u8",
            resumeSec, Array.Empty<Chapter>());

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddStreamKeeprMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }
}
