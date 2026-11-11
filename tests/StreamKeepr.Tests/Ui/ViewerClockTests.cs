using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class ViewerClockTests
{
    [Fact]
    public void Missing_cookie_uses_utc_and_marks_the_fallback()
    {
        var clock = Create();

        Assert.Equal("UTC", clock.TimeZone.Id);
        Assert.True(clock.IsFallbackToUtc);
    }

    [Fact]
    public void Cookie_zone_is_used()
    {
        var clock = Create("Europe/Berlin");

        Assert.Equal("Europe/Berlin", clock.TimeZone.Id);
        Assert.False(clock.IsFallbackToUtc);
    }

    [Fact]
    public void Encoded_cookie_zone_is_decoded()
    {
        var clock = Create(Uri.EscapeDataString("Europe/Berlin"));

        Assert.Equal("Europe/Berlin", clock.TimeZone.Id);
    }

    [Fact]
    public void Invalid_zone_uses_utc()
    {
        var clock = Create("Mars/Olympus_Mons");

        Assert.Equal("UTC", clock.TimeZone.Id);
        Assert.True(clock.IsFallbackToUtc);
    }

    [Fact]
    public void Invalid_cookie_falls_back_to_the_persisted_zone()
    {
        var store = new TestClientStateStore();
        store.Cookies[ViewerClock.CookieName] = "Mars/Olympus_Mons";
        store.Persist(ViewerClock.PersistenceKey, "Europe/Berlin");

        var clock = new ViewerClock(store, TimeProvider.System);

        Assert.Equal("Europe/Berlin", clock.TimeZone.Id);
        Assert.False(clock.IsFallbackToUtc);
    }

    [Fact]
    public void TrySetTimeZone_updates_once_and_raises_changed()
    {
        var clock = Create();
        var changes = 0;
        clock.Changed += () => changes++;

        Assert.True(clock.TrySetTimeZone("Europe/Berlin"));
        Assert.Equal(1, changes);
        Assert.False(clock.TrySetTimeZone("Europe/Berlin"));
        Assert.Equal(1, changes);
        Assert.False(clock.TrySetTimeZone("invalid/zone"));
        Assert.Equal(1, changes);
        Assert.False(clock.IsFallbackToUtc);
    }

    [Fact]
    public void Now_uses_the_viewer_zone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var clock = Create("America/New_York", time);

        Assert.Equal(new DateTimeOffset(2026, 11, 10, 7, 0, 0, TimeSpan.FromHours(-5)), clock.Now);
        Assert.Equal(TimeSpan.FromHours(-5), clock.Now.Offset);
    }

    [Fact]
    public void ToLocal_handles_both_sides_of_a_dst_change()
    {
        var clock = Create("America/New_York");

        var before = clock.ToLocal(new DateTimeOffset(2026, 3, 8, 6, 59, 0, TimeSpan.Zero));
        var after = clock.ToLocal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero));

        Assert.Equal(TimeSpan.FromHours(-5), before.Offset);
        Assert.Equal(new DateTime(2026, 3, 8, 1, 59, 0), before.DateTime);
        Assert.Equal(TimeSpan.FromHours(-4), after.Offset);
        Assert.Equal(new DateTime(2026, 3, 8, 3, 0, 0), after.DateTime);
    }

    [Fact]
    public void Prerender_zone_is_persisted_and_restored_into_the_circuit()
    {
        var prerenderStore = new TestClientStateStore();
        prerenderStore.Cookies[ViewerClock.CookieName] = "Europe/Berlin";
        _ = Create(store: prerenderStore);

        var persisted = Assert.IsType<string>(prerenderStore.Persisted[ViewerClock.PersistenceKey]);
        Assert.Equal("Europe/Berlin", persisted);

        var circuitStore = new TestClientStateStore();
        circuitStore.Persist(ViewerClock.PersistenceKey, persisted);
        var clock = Create(store: circuitStore);

        Assert.Equal("Europe/Berlin", clock.TimeZone.Id);
        Assert.False(clock.IsFallbackToUtc);
    }

    private static ViewerClock Create(
        string? cookie = null,
        TimeProvider? timeProvider = null,
        TestClientStateStore? store = null)
    {
        store ??= new TestClientStateStore();
        if (cookie is not null)
            store.Cookies[ViewerClock.CookieName] = cookie;

        return new ViewerClock(store, timeProvider ?? TimeProvider.System);
    }
}
