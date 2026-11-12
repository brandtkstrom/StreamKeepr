using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Components.Pages;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class HomeDeleteTests
{
    [Fact]
    public void A_deleted_recording_notifies_and_opens_the_next_newest()
    {
        using BunitContext context = HomePlayerTests.CreateContext(out _, out TestLibraryView view);
        SetLibrary(view);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/r1");

        context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));

        view.FindById.Remove("r1");
        view.RecordingsByLogin["ann"] = [view.FindById["r2"]];
        view.RaiseChanged();

        var snackbars = context.Services.GetRequiredService<SnackbarService>();
        Assert.Equal("Recording deleted", snackbars.Current?.Text);
        Assert.EndsWith("/r/r2", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_the_last_recording_opens_the_library()
    {
        using BunitContext context = HomePlayerTests.CreateContext(out _, out TestLibraryView view);
        SetLibrary(view);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/r1");

        context.Render<Home>(p => p.Add(c => c.RecordingId, "r1"));
        view.FindById.Remove("r1");
        view.RecordingsByLogin["ann"] = [];
        view.RaiseChanged();

        Assert.Equal("/", new Uri(navigation.Uri).AbsolutePath);
    }

    [Fact]
    public void An_unknown_initial_id_does_not_navigate_or_notify()
    {
        using BunitContext context = HomePlayerTests.CreateContext(out _, out TestLibraryView view);
        SetLibrary(view);
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/missing");

        IRenderedComponent<Home> cut = context.Render<Home>(p => p.Add(c => c.RecordingId, "missing"));

        Assert.NotNull(cut.Find("[data-unknown-recording]"));
        Assert.Equal("/r/missing", new Uri(navigation.Uri).AbsolutePath);
        Assert.Null(context.Services.GetRequiredService<SnackbarService>().Current);
    }

    private static void SetLibrary(TestLibraryView view)
    {
        RecordingView first = VideoPlayerTests.Recording("r1", "First");
        RecordingView second = VideoPlayerTests.Recording("r2", "Second");
        view.FindById["r1"] = first;
        view.FindById["r2"] = second;
        view.RecordingsByLogin["ann"] = [first, second];
        view.StreamerItems =
        [
            new StreamerView("ann", "Ann", null, false, false, true, 2, first.StartedAt),
        ];
    }
}
