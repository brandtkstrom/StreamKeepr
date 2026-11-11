using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Components.Primitives;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class SnackbarTests
{
    [Fact]
    public void Service_shows_one_message_and_queues_the_rest()
    {
        var clock = new FakeTimeProvider();
        using var service = new SnackbarService(clock);

        service.Show("First");
        service.Show("Second");

        Assert.Equal("First", service.Current?.Text);

        clock.Advance(SnackbarService.DefaultDuration);
        Assert.Equal("Second", service.Current?.Text);

        clock.Advance(SnackbarService.DefaultDuration);
        Assert.Null(service.Current);
    }

    [Fact]
    public void Dismiss_advances_the_queue_and_raises_changed()
    {
        var clock = new FakeTimeProvider();
        using var service = new SnackbarService(clock);
        var changes = 0;
        service.Changed += () => changes++;

        service.Show("First");
        service.Show("Second");
        service.Dismiss();

        Assert.Equal("Second", service.Current?.Text);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_custom_duration_controls_auto_dismissal()
    {
        var clock = new FakeTimeProvider();
        using var service = new SnackbarService(clock);

        service.Show(new SnackbarMessage("Timed", Duration: TimeSpan.FromSeconds(2)));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(service.Current);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task Invoking_the_action_dismisses_first_then_runs_it()
    {
        var clock = new FakeTimeProvider();
        using var service = new SnackbarService(clock);
        var order = new List<string>();

        service.Show(new SnackbarMessage(
            "Recording deleted",
            "Undo",
            () =>
            {
                order.Add("action");
                return Task.CompletedTask;
            }));
        service.Changed += () => order.Add("changed");

        await service.InvokeActionAsync();

        Assert.Null(service.Current);
        Assert.Equal(["changed", "action"], order);
    }

    [Fact]
    public void Invalid_messages_are_rejected()
    {
        using var service = new SnackbarService(new FakeTimeProvider());

        Assert.Throws<ArgumentException>(() => service.Show(" "));
        Assert.Throws<ArgumentException>(() =>
            service.Show(new SnackbarMessage("Text", ActionLabel: "Undo")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.Show(new SnackbarMessage("Text", Duration: TimeSpan.Zero)));
    }

    [Fact]
    public void Host_renders_the_current_message_with_status_and_action()
    {
        using var context = new BunitContext();
        var clock = new FakeTimeProvider();
        var service = new SnackbarService(clock);
        context.Services.AddSingleton(service);
        context.Services.AddSingleton<TimeProvider>(clock);

        var cut = context.Render<SnackbarHost>();
        service.Show("Recording deleted", "Undo", () => Task.CompletedTask);

        cut.WaitForAssertion(() =>
        {
            var snackbar = cut.Find(".sk-snackbar");
            Assert.Equal("status", snackbar.GetAttribute("role"));
            Assert.Contains("Recording deleted", snackbar.TextContent);
            Assert.Contains("Undo", snackbar.TextContent);
        });
    }

    [Fact]
    public void Snackbar_renders_one_action_and_a_dismiss_control()
    {
        using var context = new BunitContext();
        var action = false;
        var dismissed = false;
        var cut = context.Render<Snackbar>(parameters => parameters
            .Add(component => component.Text, "Recording deleted")
            .Add(component => component.ActionLabel, "Undo")
            .Add(component => component.OnAction,
                EventCallback.Factory.Create(this, () => action = true))
            .Add(component => component.OnDismiss,
                EventCallback.Factory.Create(this, () => dismissed = true)));

        Assert.Single(cut.FindAll(".sk-snackbar-action"));
        Assert.Equal("Dismiss notification",
            cut.Find(".sk-snackbar-dismiss").GetAttribute("aria-label"));

        cut.Find(".sk-snackbar-action").Click();
        cut.Find(".sk-snackbar-dismiss").Click();

        Assert.True(action);
        Assert.True(dismissed);
    }
}
