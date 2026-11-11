using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Domain;
using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

public sealed class RecorderJobTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "streamkeepr-tests",
        Guid.NewGuid().ToString("N"));

    public RecorderJobTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Records_one_complete_part_from_a_fake_streamlink()
    {
        string ffmpeg = RequireFfmpeg();
        Session session = NewSession();
        RecorderOptions options = NewOptions(ffmpeg);

        IReadOnlyList<DvrEvent> events = await RunAsync(session, options);

        var media = Assert.Single(events.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(2, events.Count);

        var job = new JobId(session.Id, 1);
        Assert.Equal(job, media.Job);
        Assert.Equal(job, exited.Job);
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.True(
            events.ToList().IndexOf(media) < events.ToList().IndexOf(exited),
            "PartProducingMedia must be published before RecorderExited");

        string playlistPath = Path.Combine(session.Directory, PartFiles.Playlist(1));
        string initPath = Path.Combine(session.Directory, PartFiles.Init(1));
        Assert.True(File.Exists(playlistPath), $"missing part playlist at {playlistPath}");
        Assert.True(File.Exists(initPath), $"missing init segment at {initPath}");
        Assert.True(new FileInfo(initPath).Length > 0, "init segment is empty");

        string playlist = await File.ReadAllTextAsync(playlistPath);
        Assert.StartsWith("#EXTM3U", playlist);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:EVENT", playlist);
        Assert.Contains("#EXT-X-INDEPENDENT-SEGMENTS", playlist);
        Assert.Contains("#EXT-X-PROGRAM-DATE-TIME:", playlist);
        Assert.Contains("#EXT-X-MAP:URI=\"init_001.mp4\"", playlist);
        Assert.Contains("#EXT-X-ENDLIST", playlist);

        string[] segments = Directory.GetFiles(session.Directory, "p001_*.m4s");
        Assert.NotEmpty(segments);
        Assert.All(segments, segment => Assert.True(new FileInfo(segment).Length > 0));
        Assert.Contains(Path.GetFileName(segments[0]), playlist);
        Assert.Empty(Directory.GetFiles(session.Directory, "*.tmp"));

        await AssertPlayableAsync(ffmpeg, playlistPath);
    }

    [Fact]
    public async Task Existing_part_is_reported_without_overwriting_any_file()
    {
        string ffmpeg = RequireFfmpeg();
        Session session = NewSession();
        string sentinel = Path.Combine(session.Directory, PartFiles.Init(1));
        await File.WriteAllTextAsync(sentinel, "do not touch");

        IReadOnlyList<DvrEvent> events = await RunAsync(session, NewOptions(ffmpeg));

        Assert.Empty(events.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);
        Assert.Contains("already exists", exited.Detail, StringComparison.Ordinal);

        Assert.Equal("do not touch", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(session.Directory, PartFiles.Playlist(1))));
        Assert.False(Directory.EnumerateFiles(session.Directory, "p001*").Any());
    }

    [Fact]
    public async Task A_run_without_media_publishes_no_media_event_and_deletes_part_files()
    {
        string ffmpeg = RequireFfmpeg();
        string trueCommand = FindExecutable("true")
            ?? throw new InvalidOperationException("the true command is required by this test");
        Session session = NewSession();
        RecorderOptions options = NewOptions(ffmpeg);
        options.StreamlinkPath = trueCommand;

        IReadOnlyList<DvrEvent> events = await RunAsync(session, options);

        Assert.Empty(events.OfType<DvrEvent.PartProducingMedia>());
        var exited = Assert.Single(events.OfType<DvrEvent.RecorderExited>());
        Assert.Equal(RecorderExitKind.Exited, exited.Kind);

        Assert.False(File.Exists(Path.Combine(session.Directory, PartFiles.Playlist(1))));
        Assert.False(File.Exists(Path.Combine(session.Directory, PartFiles.Init(1))));
        Assert.Empty(Directory.EnumerateFiles(session.Directory, "p001*"));
        Assert.Empty(Directory.EnumerateFiles(session.Directory, "init_001*"));
    }

    private static async Task<IReadOnlyList<DvrEvent>> RunAsync(
        Session session,
        RecorderOptions options)
    {
        var events = new List<DvrEvent>();
        ValueTask Publish(DvrEvent dvrEvent, CancellationToken _)
        {
            lock (events)
                events.Add(dvrEvent);
            return ValueTask.CompletedTask;
        }

        var job = new RecorderJob(
            Options.Create(options),
            TimeProvider.System,
            NullLogger<RecorderJob>.Instance);
        Task run = job.RunAsync(session, new JobId(session.Id, 1), Publish);
        Task completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.True(completed == run, "recorder job did not finish within 60 seconds");
        await run;

        lock (events)
            return events.ToList();
    }

    private RecorderOptions NewOptions(string ffmpeg) => new()
    {
        StreamlinkPath = FakeStreamlinkPath(),
        FfmpegPath = ffmpeg,
        Quality = "best",
        SegmentSeconds = 2,
        FinalizeTimeoutSeconds = 10,
    };

    private Session NewSession()
    {
        var live = new LiveStream(
            "123456789",
            "A test stream",
            "Just Chatting",
            DateTimeOffset.UtcNow,
            ThumbnailUrl: null);
        Session session = Session.For(live, "streamer", _root);
        Directory.CreateDirectory(session.Directory);
        return session;
    }

    private static string RequireFfmpeg()
    {
        string? ffmpeg = FindExecutable("ffmpeg");
        Assert.True(
            ffmpeg is not null,
            "ffmpeg is required for recorder integration tests. Install ffmpeg and put it on PATH.");
        return ffmpeg!;
    }

    private static string FakeStreamlinkPath()
    {
        string path = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "fake-streamlink");
        Assert.True(File.Exists(path), $"fake Streamlink script not found at {path}");

        FileAttributes attributes = File.GetAttributes(path);
        Assert.False(
            attributes.HasFlag(FileAttributes.Directory),
            $"fake Streamlink path is a directory: {path}");

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode execute = UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            Assert.True(
                (mode & execute) != 0,
                $"fake Streamlink script is not executable: {path}");
        }

        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StreamKeepr.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("could not locate the repository root from the test output");
    }

    private static string? FindExecutable(string executable)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(directory))
                continue;

            string candidate = Path.Combine(directory, executable);
            var file = new FileInfo(candidate);
            if (!file.Exists)
                continue;

            // PATH may contain a stale symlink such as a removed Homebrew keg.
            FileSystemInfo resolved = file.LinkTarget is null
                ? file
                : file.ResolveLinkTarget(returnFinalTarget: true) ?? file;
            if (resolved.Exists)
                return candidate;
        }

        return null;
    }

    private static async Task AssertPlayableAsync(string ffmpeg, string playlistPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(playlistPath);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("null");
        process.StartInfo.ArgumentList.Add("-");

        Assert.True(process.Start(), "ffmpeg failed to start for playback verification");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        string errors = await stderr;
        await stdout;

        Assert.True(
            process.ExitCode == 0,
            $"ffmpeg could not play the recorded part (exit {process.ExitCode}): {errors}");
    }
}
