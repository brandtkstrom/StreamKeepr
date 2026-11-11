using StreamKeepr.Components.Features.Player;

namespace StreamKeepr.Tests.Ui;

public sealed class PlayerBridgeTests
{
    [Fact]
    public async Task Before_attach_only_the_latest_seek_is_kept()
    {
        var bridge = new PlayerBridge();
        var seeks = new List<double>();

        await bridge.SeekAsync(10);
        await bridge.SeekAsync(25);

        Assert.False(bridge.IsReady);
        Assert.Empty(seeks);

        bridge.Attach(seconds =>
        {
            seeks.Add(seconds);
            return ValueTask.CompletedTask;
        });

        Assert.True(bridge.IsReady);
        Assert.Equal([25d], seeks);
    }

    [Fact]
    public async Task An_attached_bridge_forwards_seeks_and_clamps_negative_values()
    {
        var bridge = new PlayerBridge();
        var seeks = new List<double>();
        bridge.Attach(seconds =>
        {
            seeks.Add(seconds);
            return ValueTask.CompletedTask;
        });

        await bridge.SeekAsync(-4);
        await bridge.SeekAsync(12.5);
        await bridge.SeekAsync(double.NaN);
        await bridge.SeekAsync(double.PositiveInfinity);

        Assert.Equal([0d, 12.5], seeks);
    }

    [Fact]
    public async Task Detach_releases_the_callback_and_discards_a_pending_seek()
    {
        var bridge = new PlayerBridge();
        var seeks = new List<double>();

        await bridge.SeekAsync(30);
        bridge.Detach();

        bridge.Attach(seconds =>
        {
            seeks.Add(seconds);
            return ValueTask.CompletedTask;
        });

        Assert.True(bridge.IsReady);
        Assert.Empty(seeks);
    }

    [Fact]
    public async Task A_seek_queued_after_detach_flushes_when_the_player_reattaches()
    {
        var bridge = new PlayerBridge();
        var seeks = new List<double>();
        bridge.Attach(_ => ValueTask.CompletedTask);
        bridge.Detach();

        await bridge.SeekAsync(42);
        bridge.Attach(seconds =>
        {
            seeks.Add(seconds);
            return ValueTask.CompletedTask;
        });

        Assert.Equal([42d], seeks);
    }
}
