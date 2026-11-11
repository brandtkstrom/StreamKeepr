using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

public sealed class ChannelsViewTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly DvrStatus _status = new();
    private readonly RecordingLibrary _library = new(NullLogger<RecordingLibrary>.Instance);
    private readonly FakeControl _control = new();
    private readonly SwitchableAuth _auth = new();
    private ChannelStore _channels = null!;

    [Fact]
    public async Task Every_member_throws_without_a_signed_in_admin()
    {
        using ChannelsView view = await CreateAsync(signedIn: false);

        Assert.Throws<UnauthorizedAccessException>(() => view.All);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.AddAsync("ann", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.RemoveAsync("ann", default));
        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task Lists_watched_channels_by_display_name_then_login_with_their_state()
    {
        using ChannelsView view = await CreateAsync(signedIn: true);
        await _channels.AddAsync(new ChannelEntry("zed", DisplayName: "Alpha"), default);
        await _channels.AddAsync(new ChannelEntry("bob", DisplayName: "beta"), default);
        await _channels.AddAsync(new ChannelEntry("amy"), default);
        await _channels.AddAsync(new ChannelEntry("cat", Paused: true, DisplayName: "Cat"), default);
        await _channels.AddAsync(new ChannelEntry("dan", DisplayName: "Dan"), default);
        await _channels.AddAsync(new ChannelEntry("eve", DisplayName: "Eve"), default);
        MarkRecording("bob");
        _status.Applied("dan", null, new DvrState.CoolingDown(Now.AddMinutes(5), null, "boom"), Now);
        _library.Upsert(OpenSession("eve"));

        IReadOnlyList<ChannelView> all = view.All;

        Assert.Equal(["zed", "amy", "bob", "cat", "dan", "eve"], all.Select(c => c.Login));
        Assert.Equal("Alpha", all[0].DisplayName);
        Assert.Equal("amy", all[1].DisplayName);
        Assert.Equal(
            [ChannelRuntimeState.Watching, ChannelRuntimeState.Watching, ChannelRuntimeState.Recording,
             ChannelRuntimeState.Paused, ChannelRuntimeState.CoolingDown, ChannelRuntimeState.Reconnecting],
            all.Select(c => c.State));
        Assert.True(all[3].IsPaused);
    }

    [Fact]
    public async Task Add_and_remove_go_through_channel_control()
    {
        using ChannelsView view = await CreateAsync(signedIn: true);

        await view.AddAsync("ann", default);
        await view.RemoveAsync("ann", default);

        Assert.Equal(["add:ann", "remove:ann"], _control.Calls);
    }

    [Fact]
    public async Task Source_changes_raise_one_throttled_changed_and_auth_changes_raise_at_once()
    {
        using ChannelsView view = await CreateAsync(signedIn: true);
        int changed = 0;
        view.Changed += () => changed++;

        await _channels.AddAsync("ann", default);
        MarkRecording("ann");
        Assert.Equal(0, changed);
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, changed);

        _auth.Set(signedIn: false);
        Assert.Equal(2, changed);
        Assert.Throws<UnauthorizedAccessException>(() => view.All);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<ChannelsView> CreateAsync(bool signedIn)
    {
        _channels = new ChannelStore(
            Options.Create(new TwitchOptions()),
            Options.Create(new StorageOptions { RecordingsRoot = _root }),
            NullLogger<ChannelStore>.Instance);
        await _channels.LoadAsync(default);
        _auth.Set(signedIn);
        return new ChannelsView(_channels, _status, _library, _control, _auth, _clock);
    }

    private void MarkRecording(string login)
    {
        var live = new LiveStream("1", "Title", "Game", Now, ThumbnailUrl: null);
        var session = new Session($"20261111T110000Z_{login}1", login, "1", Now, _root);
        _status.Applied(
            login, null,
            new DvrState.Recording(session, new JobId(session.Id, 1), live, 0, true, 0), Now);
    }

    private static RecordingSummary OpenSession(string login) =>
        new($"s-{login}", login, "Title", "Game", Now.AddHours(-1), null, IsRecording: true,
            DurationSeconds: null, SizeBytes: null, PartCount: 1, SegmentCount: 1,
            HasThumbnail: false, Chapters: []);

    private sealed class FakeControl : IChannelControl
    {
        public List<string> Calls { get; } = [];

        public Task AddChannelAsync(string login, CancellationToken ct)
        {
            Calls.Add($"add:{login}");
            return Task.CompletedTask;
        }

        public Task RemoveChannelAsync(string login, CancellationToken ct)
        {
            Calls.Add($"remove:{login}");
            return Task.CompletedTask;
        }

        public Task StopRecordingAsync(string login, CancellationToken ct) => Task.CompletedTask;

        public Task ResumeRecordingAsync(string login, CancellationToken ct) => Task.CompletedTask;
    }

    internal sealed class SwitchableAuth : AuthenticationStateProvider
    {
        private Task<AuthenticationState> _state = Anonymous();

        public void Set(bool signedIn)
        {
            _state = signedIn
                ? Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.Name, "mock_admin")], "test"))))
                : Anonymous();
            NotifyAuthenticationStateChanged(_state);
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state;

        private static Task<AuthenticationState> Anonymous() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
