using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Runtime;
using StreamKeepr.Infrastructure.Storage;
using StreamKeepr.Infrastructure.Twitch;
using StreamKeepr.Tests.Hosting;

namespace StreamKeepr.Tests.Runtime;

public sealed class ChannelControlTests
{
    private const string Channel = "streamer";

    private static readonly DateTimeOffset StartedAt =
        new(2026, 11, 10, 16, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Add_resolves_the_profile_persists_and_starts_recording()
    {
        string ffmpeg = RequireFfmpeg();
        string root = NewRoot();

        try
        {
            var poller = new ScriptedStreamPoller();
            poller.Profiles["new_channel"] = new ChannelProfile(
                "123456", "New Channel", "https://example/avatar.png");

            using var factory = CreateFactory(root, poller, ffmpeg);
            using var client = factory.CreateClient();
            IChannelControl control = factory.Services.GetRequiredService<IChannelControl>();
            var store = factory.Services.GetRequiredService<ChannelStore>();
            DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();

            await control.AddChannelAsync(" New_Channel ", CancellationToken.None);

            ChannelEntry entry = Assert.Single(store.Snapshot);
            Assert.Equal("new_channel", entry.Login);
            Assert.Equal("123456", entry.UserId);
            Assert.Equal("New Channel", entry.DisplayName);
            Assert.Equal("https://example/avatar.png", entry.AvatarUrl);
            Assert.True(poller.Nudges > 0);

            poller.Send("new_channel", new StreamObservation.Live(
                new LiveStream("555", "New stream", "Just Chatting", StartedAt, null)));

            DvrState.Recording recording = await WaitForRecordingAsync(
                status, "new_channel");
            await WaitForMediaAsync(recording.Session.Directory);
            Assert.Equal(1, recording.Job.Part);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Add_unknown_login_fails_without_persisting()
    {
        string ffmpeg = RequireFfmpeg();
        string root = NewRoot();

        try
        {
            var poller = new ScriptedStreamPoller();
            using var factory = CreateFactory(root, poller, ffmpeg);
            using var client = factory.CreateClient();
            IChannelControl control = factory.Services.GetRequiredService<IChannelControl>();
            var store = factory.Services.GetRequiredService<ChannelStore>();

            await Assert.ThrowsAsync<UnknownChannelException>(
                () => control.AddChannelAsync("missing", CancellationToken.None));

            Assert.Empty(store.Snapshot);
            Assert.Equal(0, poller.Nudges);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Remove_while_recording_finalizes_and_keeps_the_recording()
    {
        string ffmpeg = RequireFfmpeg();
        string root = NewRoot();

        try
        {
            var poller = new ScriptedStreamPoller();
            using var factory = CreateFactory(root, poller, ffmpeg, Channel);
            using var client = factory.CreateClient();
            IChannelControl control = factory.Services.GetRequiredService<IChannelControl>();
            var store = factory.Services.GetRequiredService<ChannelStore>();
            var library = factory.Services.GetRequiredService<RecordingLibrary>();
            DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();

            await poller.Started.WaitAsync(TimeSpan.FromSeconds(10));
            poller.Send(Channel, new StreamObservation.Live(
                new LiveStream("556", "Removed stream", "Just Chatting", StartedAt, null)));
            DvrState.Recording recording = await WaitForRecordingAsync(status, Channel);
            await WaitForMediaAsync(recording.Session.Directory);

            await control.RemoveChannelAsync(Channel, CancellationToken.None);

            RecordingSummary summary = await RuntimeTestWait.ForValueAsync(
                () => library.Find(recording.Session.Id) is { IsRecording: false } found
                    ? found
                    : null,
                TimeSpan.FromSeconds(30));

            Assert.Empty(store.Snapshot);
            Assert.False(status.Current.ContainsKey(Channel));
            Assert.True(File.Exists(Path.Combine(recording.Session.Directory, "index.m3u8")));
            Assert.Equal(recording.Session.Id, summary.Id);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Stop_then_resume_continues_the_same_session_as_the_next_part()
    {
        string ffmpeg = RequireFfmpeg();
        string root = NewRoot();

        try
        {
            var poller = new ScriptedStreamPoller();
            using var factory = CreateFactory(root, poller, ffmpeg, Channel);
            using var client = factory.CreateClient();
            IChannelControl control = factory.Services.GetRequiredService<IChannelControl>();
            var store = factory.Services.GetRequiredService<ChannelStore>();
            var library = factory.Services.GetRequiredService<RecordingLibrary>();
            DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();

            await poller.Started.WaitAsync(TimeSpan.FromSeconds(10));
            poller.Send(Channel, new StreamObservation.Live(
                new LiveStream("557", "Stopped stream", "Just Chatting", StartedAt, null)));
            DvrState.Recording recording = await WaitForRecordingAsync(status, Channel);
            await WaitForMediaAsync(recording.Session.Directory);

            await control.StopRecordingAsync(Channel, CancellationToken.None);

            await RuntimeTestWait.ForStatusAsync(
                status, Channel, current => current.State is DvrState.Paused);
            await RuntimeTestWait.ForValueAsync(
                () => library.Find(recording.Session.Id) is { IsRecording: false } found
                    ? found
                    : null,
                TimeSpan.FromSeconds(30));
            Assert.True(store.Snapshot.Single().Paused);

            Dictionary<string, byte[]> partOne = CapturePart(recording.Session.Directory, 1);

            int nudgesBeforeResume = poller.Nudges;
            await control.ResumeRecordingAsync(Channel, CancellationToken.None);
            Assert.False(store.Snapshot.Single().Paused);
            Assert.True(poller.Nudges > nudgesBeforeResume);

            poller.Send(Channel, new StreamObservation.Live(
                new LiveStream("557", "Stopped stream", "Just Chatting", StartedAt, null)));

            DvrState.Recording resumed = await WaitForRecordingAsync(
                status, Channel, part: 2);
            Assert.Equal(recording.Session.Id, resumed.Session.Id);

            await RuntimeTestWait.UntilAsync(
                () => File.Exists(Path.Combine(recording.Session.Directory, "p002.m3u8")),
                TimeSpan.FromSeconds(30));

            foreach ((string name, byte[] bytes) in partOne)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(
                    Path.Combine(recording.Session.Directory, name)));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Paused_channel_stays_paused_after_restart_until_resumed()
    {
        string ffmpeg = RequireFfmpeg();
        string root = NewRoot();

        try
        {
            string sessionId;
            Dictionary<string, byte[]> partOne;

            {
                var poller = new ScriptedStreamPoller();
                using var factory = CreateFactory(root, poller, ffmpeg, Channel);
                using var client = factory.CreateClient();
                IChannelControl control = factory.Services.GetRequiredService<IChannelControl>();
                DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();
                var library = factory.Services.GetRequiredService<RecordingLibrary>();

                await poller.Started.WaitAsync(TimeSpan.FromSeconds(10));
                poller.Send(Channel, new StreamObservation.Live(
                    new LiveStream("558", "Paused stream", "Just Chatting", StartedAt, null)));
                DvrState.Recording recording = await WaitForRecordingAsync(status, Channel);
                await WaitForMediaAsync(recording.Session.Directory);
                sessionId = recording.Session.Id;

                await control.StopRecordingAsync(Channel, CancellationToken.None);
                await RuntimeTestWait.ForStatusAsync(
                    status, Channel, current => current.State is DvrState.Paused);
                await RuntimeTestWait.ForValueAsync(
                    () => library.Find(sessionId) is { IsRecording: false } found
                        ? found
                        : null,
                    TimeSpan.FromSeconds(30));
                partOne = CapturePart(recording.Session.Directory, 1);
            }

            var secondPoller = new ScriptedStreamPoller();
            using (var factory = CreateFactory(root, secondPoller, ffmpeg, Channel))
            using (var client = factory.CreateClient())
            {
                IChannelControl control = factory.Services.GetRequiredService<IChannelControl>();
                var store = factory.Services.GetRequiredService<ChannelStore>();
                DvrStatus status = factory.Services.GetRequiredService<DvrStatus>();

                await RuntimeTestWait.ForStatusAsync(
                    status, Channel, current => current.State is DvrState.Paused);
                Assert.True(store.Snapshot.Single().Paused);

                secondPoller.Send(Channel, new StreamObservation.Live(
                    new LiveStream("558", "Paused stream", "Just Chatting", StartedAt, null)));
                await RuntimeTestWait.ForStatusAsync(
                    status,
                    Channel,
                    current => current.State is DvrState.Paused
                        && current.LastObservation is StreamObservation.Live);
                Assert.IsType<DvrState.Paused>(status.Current[Channel].State);

                await control.ResumeRecordingAsync(Channel, CancellationToken.None);
                secondPoller.Send(Channel, new StreamObservation.Live(
                    new LiveStream("558", "Paused stream", "Just Chatting", StartedAt, null)));

                DvrState.Recording resumed = await WaitForRecordingAsync(
                    status, Channel, part: 2);
                Assert.Equal(sessionId, resumed.Session.Id);
                await RuntimeTestWait.UntilAsync(
                    () => File.Exists(Path.Combine(resumed.Session.Directory, "p002.m3u8")),
                    TimeSpan.FromSeconds(30));

                foreach ((string name, byte[] bytes) in partOne)
                {
                    Assert.Equal(bytes, await File.ReadAllBytesAsync(
                        Path.Combine(resumed.Session.Directory, name)));
                }
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Unknown_invalid_and_duplicate_operations_fail_without_persisting()
    {
        string ffmpeg = RequireFfmpeg();
        string root = NewRoot();

        try
        {
            var poller = new ScriptedStreamPoller();
            poller.Profiles["dup"] = new ChannelProfile("1", "Dup", null);

            using var factory = CreateFactory(root, poller, ffmpeg);
            using var client = factory.CreateClient();
            IChannelControl control = factory.Services.GetRequiredService<IChannelControl>();
            var store = factory.Services.GetRequiredService<ChannelStore>();

            await Assert.ThrowsAsync<ArgumentException>(
                () => control.AddChannelAsync("BAD!", CancellationToken.None));
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => control.RemoveChannelAsync("missing", CancellationToken.None));
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => control.StopRecordingAsync("missing", CancellationToken.None));
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => control.ResumeRecordingAsync("missing", CancellationToken.None));
            Assert.Empty(store.Snapshot);

            await control.AddChannelAsync("dup", CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => control.AddChannelAsync("dup", CancellationToken.None));
            Assert.Single(store.Snapshot);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Stop_waits_behind_a_busy_control_gate()
    {
        string ffmpeg = RequireFfmpeg();
        string root = NewRoot();

        try
        {
            var poller = new GatePoller();
            poller.Profiles["slow"] = new ChannelProfile("1", "Slow", null);

            using var factory = CreateFactory(root, poller, ffmpeg, Channel);
            using var client = factory.CreateClient();
            IChannelControl control = factory.Services.GetRequiredService<IChannelControl>();
            var store = factory.Services.GetRequiredService<ChannelStore>();

            Task add = control.AddChannelAsync("slow", CancellationToken.None);
            await poller.SlowLookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task stop = control.StopRecordingAsync(Channel, CancellationToken.None);
            Assert.False(stop.IsCompleted);

            poller.Gate.TrySetResult();
            await Task.WhenAll(add, stop);

            Assert.True(store.Snapshot.Single(entry => entry.Login == Channel).Paused);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Concurrent_adds_serialize_on_the_control_gate()
    {
        string ffmpeg = RequireFfmpeg();
        string root = NewRoot();

        try
        {
            var poller = new GatePoller();
            poller.Profiles["slow"] = new ChannelProfile("1", "Slow", null);
            poller.Profiles["fast"] = new ChannelProfile("2", "Fast", null);

            using var factory = CreateFactory(root, poller, ffmpeg);
            using var client = factory.CreateClient();
            IChannelControl control = factory.Services.GetRequiredService<IChannelControl>();
            var store = factory.Services.GetRequiredService<ChannelStore>();

            Task slow = control.AddChannelAsync("slow", CancellationToken.None);
            await poller.SlowLookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task fast = control.AddChannelAsync("fast", CancellationToken.None);
            Assert.False(poller.FastLookupStarted.Task.IsCompleted);
            Assert.False(fast.IsCompleted);

            poller.Gate.TrySetResult();
            await Task.WhenAll(slow, fast);

            Assert.Equal(2, store.Snapshot.Count);
            Assert.True(poller.FastLookupStarted.Task.IsCompleted);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static StreamKeeprAppFactory CreateFactory(
        string root, IStreamPoller poller, string ffmpeg, params string[] channels)
    {
        var overrides = new Dictionary<string, string?>
        {
            [$"{RecorderOptions.SectionName}:StreamlinkPath"] =
                TestPaths.FixturePath("fake-live-streamlink"),
            [$"{RecorderOptions.SectionName}:FfmpegPath"] = ffmpeg,
            [$"{RecorderOptions.SectionName}:SegmentSeconds"] = "2",
            [$"{RecorderOptions.SectionName}:FinalizeTimeoutSeconds"] = "5",
            [$"{RecorderOptions.SectionName}:StallTimeoutSeconds"] = "30",
            [$"{RecorderOptions.SectionName}:RetryBaseSeconds"] = "60",
            [$"{RecorderOptions.SectionName}:RetryMaxSeconds"] = "600",
            [$"{StorageOptions.SectionName}:MinFreeGb"] = "1",
        };

        for (int index = 0; index < channels.Length; index++)
            overrides[$"{TwitchOptions.SectionName}:Channels:{index}"] = channels[index];

        return new StreamKeeprAppFactory(
            overrides,
            recordingsRoot: root,
            configureTestServices: services => services.AddSingleton(poller));
    }

    private static async Task<DvrState.Recording> WaitForRecordingAsync(
        DvrStatus status, string login, int? part = null) =>
        await RuntimeTestWait.ForValueAsync(
            () => status.Current.TryGetValue(login, out DvrSnapshot? snapshot)
                && snapshot.State is DvrState.Recording recording
                && (part is null || recording.Job.Part == part)
                ? recording
                : null,
            TimeSpan.FromSeconds(30));

    private static Task WaitForMediaAsync(string directory) =>
        RuntimeTestWait.UntilAsync(
            () => File.Exists(Path.Combine(directory, "p001.m3u8"))
                && Directory.GetFiles(directory, "p001_*.m4s").Length > 0,
            TimeSpan.FromSeconds(30));

    private static Dictionary<string, byte[]> CapturePart(string directory, int part)
    {
        string playlist = $"p{part:D3}";
        string init = $"init_{part:D3}";

        return Directory.EnumerateFiles(directory)
            .Where(file =>
            {
                string name = Path.GetFileName(file);
                return name.StartsWith(playlist, StringComparison.Ordinal)
                    || name.StartsWith(init, StringComparison.Ordinal);
            })
            .ToDictionary(
                file => Path.GetFileName(file),
                File.ReadAllBytes,
                StringComparer.Ordinal);
    }

    private static string RequireFfmpeg() => TestPaths.RequireExecutable(
        "ffmpeg",
        "ffmpeg is required for channel-control tests. Install ffmpeg and put it on PATH.");

    private static string NewRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class GatePoller : IStreamPoller
    {
        public Dictionary<string, ChannelProfile> Profiles { get; } =
            new(StringComparer.Ordinal);

        public TaskCompletionSource SlowLookupStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FastLookupStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Nudges { get; private set; }

        public async Task RunAsync(
            Func<IReadOnlyList<string>> logins,
            StreamObservationRoute route,
            CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        public Task<ChannelInfo?> GetChannelInfoAsync(
            string broadcasterId, CancellationToken ct) =>
            Task.FromResult<ChannelInfo?>(null);

        public async Task<ChannelProfile?> GetUserAsync(string login, CancellationToken ct)
        {
            if (login == "slow")
            {
                SlowLookupStarted.TrySetResult();
                await Gate.Task.WaitAsync(ct);
            }
            else
            {
                FastLookupStarted.TrySetResult();
            }

            return Profiles.GetValueOrDefault(login);
        }

        public void Nudge() => Nudges++;
    }
}
