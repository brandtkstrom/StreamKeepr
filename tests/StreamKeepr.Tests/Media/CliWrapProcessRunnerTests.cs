using System.Text;
using StreamKeepr.Infrastructure.Media;

namespace StreamKeepr.Tests.Media;

/// <summary>
/// The production adapter at the recorder's process seam, exercised with real child processes.
/// Recorder job behaviour is covered through the fake runner in <see cref="RecorderJobTests"/>.
/// </summary>
public sealed class CliWrapProcessRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "streamkeepr-tests",
        Guid.NewGuid().ToString("N"));

    private readonly CliWrapProcessRunner _runner = new();

    public CliWrapProcessRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Output_lines_reach_their_callbacks_and_the_exit_code_is_reported()
    {
        string script = WriteScript("lines", "#!/bin/sh\necho out-1\necho err-1 >&2\necho out-2\nexit 3\n");
        var stdout = new List<string>();
        var stderr = new List<string>();

        RunningProcess process = _runner.Start(
            new ToolLaunch("ffmpeg", script, [], _root),
            line =>
            {
                lock (stdout)
                    stdout.Add(line);
                return ValueTask.CompletedTask;
            },
            line =>
            {
                lock (stderr)
                    stderr.Add(line);
            },
            CancellationToken.None,
            CancellationToken.None);

        ProcessOutcome outcome = await process.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(process.ProcessId);
        Assert.Equal(process.ProcessId, outcome.ProcessId);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Null(outcome.Error);
        Assert.False(outcome.Cancelled);
        Assert.Equal(["out-1", "out-2"], stdout);
        Assert.Equal(["err-1"], stderr);
    }

    [Fact]
    public async Task Standard_input_and_output_streams_carry_bytes_through_the_process()
    {
        string script = WriteScript("copy", "#!/bin/sh\nexec cat\n");
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("payload-bytes"));
        using var output = new MemoryStream();

        RunningProcess process = _runner.Start(
            new ToolLaunch("streamlink", script, [], _root, input, output),
            onStdoutLine: null,
            _ => { },
            CancellationToken.None,
            CancellationToken.None);

        ProcessOutcome outcome = await process.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("payload-bytes", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task A_force_kill_ends_a_process_that_ignores_its_graceful_signal()
    {
        using var graceful = new CancellationTokenSource();
        using var force = new CancellationTokenSource();

        RunningProcess process = _runner.Start(
            new ToolLaunch(
                "streamlink", TestPaths.FixturePath("fake-hanging-streamlink"), [], _root),
            _ => ValueTask.CompletedTask,
            _ => { },
            force.Token,
            graceful.Token);

        await WaitForFileAsync(Path.Combine(_root, "streamlink.started"));

        // The identity is captured at start and matches the live process.
        Assert.NotNull(process.ProcessId);
        Assert.NotNull(process.StartTime);
        Assert.Equal(
            ProcessIdentityKind.Alive,
            ProcessIdentityInspector.Inspect(process.ProcessId, process.StartTime).Kind);

        await graceful.CancelAsync();
        Task signalled = await Task.WhenAny(process.Completion, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(process.Completion, signalled);

        await force.CancelAsync();
        ProcessOutcome outcome = await process.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(outcome.Cancelled);
        Assert.Null(outcome.ExitCode);
        Assert.True(
            ProcessIdentityInspector.Inspect(process.ProcessId, process.StartTime).IsVerifiedGone,
            "a force-killed process must no longer own its captured PID");
    }

    [Fact]
    public async Task A_graceful_stop_ends_a_process_that_handles_its_signal()
    {
        string script = WriteScript(
            "graceful",
            "#!/bin/sh\ntrap 'echo graceful >&2; exit 0' INT\n: > graceful.started\n"
            + "while :; do sleep 0.05; done\n");
        var stderr = new List<string>();
        using var graceful = new CancellationTokenSource();
        using var force = new CancellationTokenSource();

        RunningProcess process = _runner.Start(
            new ToolLaunch("streamlink", script, [], _root),
            _ => ValueTask.CompletedTask,
            line =>
            {
                lock (stderr)
                    stderr.Add(line);
            },
            force.Token,
            graceful.Token);

        await WaitForFileAsync(Path.Combine(_root, "graceful.started"));
        await graceful.CancelAsync();

        // CliWrap delivers the graceful signal as Ctrl+C, which needs an attachable console; a
        // Windows test host has none, so there the stop must still end through the force kill.
        if (OperatingSystem.IsWindows())
            await force.CancelAsync();

        ProcessOutcome outcome = await process.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(outcome.Cancelled);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(["graceful"], stderr);
    }

    [Fact]
    public async Task Completion_waits_for_a_descendant_that_still_holds_the_output_open()
    {
        string script = WriteScript(
            "parent",
            "#!/bin/sh\n"
            + "( while [ ! -f release ]; do sleep 0.05; done; echo child-done ) &\n"
            + "echo parent-done\n"
            + ": > parent.ready\n"
            + "while [ ! -f parent.go ]; do sleep 0.05; done\n");
        var stdout = new List<string>();

        RunningProcess process = _runner.Start(
            new ToolLaunch("streamlink", script, [], _root),
            line =>
            {
                lock (stdout)
                    stdout.Add(line);
                return ValueTask.CompletedTask;
            },
            _ => { },
            CancellationToken.None,
            CancellationToken.None);

        await WaitForFileAsync(Path.Combine(_root, "parent.ready"));
        Assert.NotNull(process.StartTime);
        await File.WriteAllTextAsync(Path.Combine(_root, "parent.go"), string.Empty);
        await WaitForAsync(
            () => ProcessIdentityInspector.Inspect(process.ProcessId, process.StartTime).IsVerifiedGone,
            "the direct process to exit");

        // The direct PID is gone, but the descendant still holds stdout open: the task must stay
        // pending, which is why a stop is verified by the task and not by the PID alone.
        Assert.False(process.Completion.IsCompleted);

        await File.WriteAllTextAsync(Path.Combine(_root, "release"), string.Empty);
        ProcessOutcome outcome = await process.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(["parent-done", "child-done"], stdout);
    }

    [Fact]
    public async Task A_missing_executable_completes_with_an_error_and_no_process_identity()
    {
        RunningProcess process = _runner.Start(
            new ToolLaunch("ffmpeg", Path.Combine(_root, "does-not-exist"), [], _root),
            _ => ValueTask.CompletedTask,
            _ => { },
            CancellationToken.None,
            CancellationToken.None);

        ProcessOutcome outcome = await process.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(process.ProcessId);
        Assert.Null(process.StartTime);
        Assert.NotNull(outcome.Error);
        Assert.Null(outcome.ExitCode);
    }

    [Fact]
    public void Standard_output_needs_exactly_one_of_a_stream_or_a_line_callback()
    {
        string script = WriteScript("unused", "#!/bin/sh\nexit 0\n");
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() => _runner.Start(
            new ToolLaunch("ffmpeg", script, [], _root),
            onStdoutLine: null,
            _ => { },
            CancellationToken.None,
            CancellationToken.None));
        Assert.Throws<ArgumentException>(() => _runner.Start(
            new ToolLaunch("streamlink", script, [], _root, StandardOutput: output),
            _ => ValueTask.CompletedTask,
            _ => { },
            CancellationToken.None,
            CancellationToken.None));
    }

    private string WriteScript(string name, string body)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, body);
        return TestPaths.StartableScriptPath(path);
    }

    private static Task WaitForFileAsync(string path) =>
        WaitForAsync(() => File.Exists(path), path);

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        throw new TimeoutException($"timed out waiting for {description}");
    }
}
