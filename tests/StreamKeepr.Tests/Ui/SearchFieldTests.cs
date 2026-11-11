using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Components.Features.Library;

namespace StreamKeepr.Tests.Ui;

public sealed class SearchFieldTests
{
    [Fact]
    public void Prerenders_the_url_query_so_reload_restores_it()
    {
        using BunitContext context = CreateContext();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/?q=chef%20ann");

        IRenderedComponent<SearchField> cut = context.Render<SearchField>();

        Assert.Equal("chef ann", cut.Find("input[data-search-input]").GetAttribute("value"));
        Assert.Equal("Search recordings", cut.Find("input").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task A_debounced_search_replaces_q_and_keeps_the_rest_of_the_url()
    {
        using BunitContext context = CreateContext();
        var navigation = (BunitNavigationManager)context.Services
            .GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/r/abc?state=saved");

        IRenderedComponent<SearchField> cut = context.Render<SearchField>();
        await cut.InvokeAsync(() => cut.Instance.Search("  chef "));

        Assert.EndsWith("/r/abc?q=chef&state=saved", navigation.Uri);
        Assert.True(navigation.History.First().Options.ReplaceHistoryEntry);

        await cut.InvokeAsync(() => cut.Instance.Search(""));
        Assert.EndsWith("/r/abc?state=saved", navigation.Uri);
    }

    [Fact]
    public void Url_changes_from_elsewhere_are_mirrored_into_the_field()
    {
        using BunitContext context = CreateContext();
        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        BunitJSModuleInterop module = context.JSInterop.SetupModule(
            "./Components/Features/Library/SearchField.razor.js");
        module.SetupVoid("init", _ => true);
        module.SetupVoid("setValue", _ => true);

        context.Render<SearchField>();
        navigation.NavigateTo("/?q=art");

        Assert.Contains(
            module.Invocations,
            call => call.Identifier == "setValue" && Equals(call.Arguments[1], "art"));
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLogging();
        return context;
    }
}
