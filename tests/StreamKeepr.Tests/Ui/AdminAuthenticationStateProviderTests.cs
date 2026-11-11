using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class AdminAuthenticationStateProviderTests
{
    [Fact]
    public async Task A_rejected_session_signs_the_circuit_out_after_thirty_minutes()
    {
        var clock = new CountingClock();
        var validator = new FakeValidator { Valid = false };
        var session = new AdminSessionState();
        using var provider = new AdminAuthenticationStateProvider(
            validator, session, clock, NullLogger<AdminAuthenticationStateProvider>.Instance);
        int changes = 0;
        provider.AuthenticationStateChanged += _ => changes++;

        provider.SetAuthenticationState(SignedIn("mock_admin"));
        await WaitForTimerAsync(clock, 1);

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(0, validator.Calls);
        Assert.True((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);

        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => session.Expired);

        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        Assert.Equal("mock_admin", session.LastLogin);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task A_valid_session_stays_signed_in_and_anonymous_users_are_not_checked()
    {
        var clock = new CountingClock();
        var validator = new FakeValidator { Valid = true };
        var session = new AdminSessionState();
        using var provider = new AdminAuthenticationStateProvider(
            validator, session, clock, NullLogger<AdminAuthenticationStateProvider>.Instance);

        provider.SetAuthenticationState(SignedIn("mock_admin"));
        await WaitForTimerAsync(clock, 1);
        clock.Advance(AdminAuthenticationStateProvider.RevalidationInterval);
        await WaitUntilAsync(() => validator.Calls == 1);
        Assert.True((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        Assert.False(session.Expired);

        // Signed out: the next interval passes without a validation call.
        provider.SetAuthenticationState(Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        await WaitForTimerAsync(clock, 2);
        clock.Advance(AdminAuthenticationStateProvider.RevalidationInterval);
        await WaitForTimerAsync(clock, 3);
        Assert.Equal(1, validator.Calls);
    }

    [Fact]
    public async Task A_validator_failure_keeps_the_admin_signed_in()
    {
        var clock = new CountingClock();
        var validator = new FakeValidator { Throws = true };
        var session = new AdminSessionState();
        using var provider = new AdminAuthenticationStateProvider(
            validator, session, clock, NullLogger<AdminAuthenticationStateProvider>.Instance);

        provider.SetAuthenticationState(SignedIn("mock_admin"));
        await WaitForTimerAsync(clock, 1);
        clock.Advance(AdminAuthenticationStateProvider.RevalidationInterval);
        await WaitUntilAsync(() => validator.Calls == 1);

        Assert.True((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        Assert.False(session.Expired);
    }

    internal static Task<AuthenticationState> SignedIn(string login) =>
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, login)], "test"))));

    /// <summary>Waits until the provider has armed its next 30-minute delay on the clock.</summary>
    private static Task WaitForTimerAsync(CountingClock clock, int timers) =>
        WaitUntilAsync(() => clock.TimersCreated >= timers);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
            await Task.Delay(5);
        Assert.True(condition());
    }

    /// <summary>Delegates to a fake clock and counts the timers created on it.</summary>
    private sealed class CountingClock : TimeProvider
    {
        private int _timers;

        public FakeTimeProvider Fake { get; } = new();

        public int TimersCreated => Volatile.Read(ref _timers);

        public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();

        public override long GetTimestamp() => Fake.GetTimestamp();

        public override long TimestampFrequency => Fake.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = Fake.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }

        public void Advance(TimeSpan by) => Fake.Advance(by);
    }

    private sealed class FakeValidator : IAdminSessionValidator
    {
        public bool Valid { get; init; }

        public bool Throws { get; init; }

        public int Calls { get; private set; }

        public Task<bool> IsValidAsync(ClaimsPrincipal user, CancellationToken ct)
        {
            Calls++;
            return Throws ? throw new InvalidOperationException("boom") : Task.FromResult(Valid);
        }
    }
}
