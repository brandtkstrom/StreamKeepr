using MudBlazor;
using MudBlazor.Services;

namespace StreamKeepr.Tests.Ui;

/// <summary>
/// Stands in for MudBlazor's JS-backed viewport service so a bUnit test can put the shell at
/// a breakpoint: MudBreakpointProvider, MudHidden and MudDrawer all subscribe through it.
/// </summary>
internal sealed class TestBrowserViewport(Breakpoint breakpoint = Breakpoint.Lg) : IBrowserViewportService
{
    private readonly Dictionary<Guid, Func<BrowserViewportEventArgs, Task>> _observers = [];

    public Breakpoint Breakpoint { get; private set; } = breakpoint;

    public ResizeOptions ResizeOptions { get; } = new();

    public async Task SetBreakpointAsync(Breakpoint value)
    {
        Breakpoint = value;
        foreach ((Guid id, Func<BrowserViewportEventArgs, Task> notify) in _observers.ToArray())
            await notify(Args(id, isImmediate: false));
    }

    public Task SubscribeAsync(IBrowserViewportObserver observer, bool fireImmediately = true) =>
        SubscribeAsync(observer.Id, observer.NotifyBrowserViewportChangeAsync, observer.ResizeOptions, fireImmediately);

    public Task SubscribeAsync(Guid observerId, Action<BrowserViewportEventArgs> lambda, ResizeOptions? options = null, bool fireImmediately = true) =>
        SubscribeAsync(observerId, args => { lambda(args); return Task.CompletedTask; }, options, fireImmediately);

    public async Task SubscribeAsync(Guid observerId, Func<BrowserViewportEventArgs, Task> lambda, ResizeOptions? options = null, bool fireImmediately = true)
    {
        _observers[observerId] = lambda;
        if (fireImmediately)
            await lambda(Args(observerId, isImmediate: true));
    }

    public Task UnsubscribeAsync(IBrowserViewportObserver observer) => UnsubscribeAsync(observer.Id);

    public Task UnsubscribeAsync(Guid observerId)
    {
        _observers.Remove(observerId);
        return Task.CompletedTask;
    }

    public Task<bool> IsMediaQueryMatchAsync(string mediaQuery) => Task.FromResult(false);

    public Task<bool> IsBreakpointWithinWindowSizeAsync(Breakpoint value) =>
        Task.FromResult(Within(value, Breakpoint));

    public Task<bool> IsBreakpointWithinReferenceSizeAsync(Breakpoint value, Breakpoint reference) =>
        Task.FromResult(Within(value, reference));

    public Task<Breakpoint> GetCurrentBreakpointAsync() => Task.FromResult(Breakpoint);

    public Task<BrowserWindowSize> GetCurrentBrowserWindowSizeAsync() => Task.FromResult(Size());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private BrowserViewportEventArgs Args(Guid id, bool isImmediate) => new(id, Size(), Breakpoint, isImmediate);

    private BrowserWindowSize Size() => new()
    {
        Width = Breakpoint switch
        {
            Breakpoint.Xs => 375,
            Breakpoint.Sm => 700,
            Breakpoint.Md => 1000,
            Breakpoint.Lg => 1280,
            _ => 2000,
        },
        Height = 812,
    };

    private static bool Within(Breakpoint value, Breakpoint reference) => value switch
    {
        Breakpoint.MdAndUp => reference is Breakpoint.Md or Breakpoint.Lg or Breakpoint.Xl or Breakpoint.Xxl,
        Breakpoint.SmAndDown => reference is Breakpoint.Xs or Breakpoint.Sm,
        Breakpoint.Always => true,
        _ => value == reference,
    };
}
