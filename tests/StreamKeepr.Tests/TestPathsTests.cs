using System.Diagnostics;

namespace StreamKeepr.Tests;

public sealed class TestPathsTests
{
    [Fact]
    public void Find_executable_returns_null_when_no_variant_exists()
    {
        using var temp = new TempDirectory();

        Assert.Null(TestPaths.FindExecutable("sk-tool", temp.Path));
    }

    [Fact]
    public void Find_executable_uses_the_platform_executable_naming_rules()
    {
        using var temp = new TempDirectory();
        bool windows = OperatingSystem.IsWindows();
        string tool = Path.Combine(temp.Path, windows ? "sk-tool.cmd" : "sk-tool");
        File.WriteAllText(tool, windows ? "@exit /b 0\r\n" : "");

        Assert.Equal(tool, TestPaths.FindExecutable("sk-tool", temp.Path), ignoreCase: true);

        if (windows)
        {
            string executable = Path.Combine(temp.Path, "sk-tool-exe.exe");
            File.WriteAllText(executable, "");
            Assert.Equal(tool, TestPaths.FindExecutable("sk-tool.cmd", temp.Path), ignoreCase: true);
            Assert.Equal(
                executable, TestPaths.FindExecutable("sk-tool-exe", temp.Path), ignoreCase: true);
        }
        else
        {
            // The Windows executable extensions carry no meaning on POSIX.
            Assert.Null(TestPaths.FindExecutable("sk-tool.exe", temp.Path));
        }
    }

    [Fact]
    public void Require_executable_throws_the_setup_message_when_the_tool_is_missing()
    {
        using var temp = new TempDirectory();
        const string message = "ffmpeg is required for export tests. Install it and put it on PATH.";

        var error = Assert.Throws<Xunit.Sdk.TrueException>(
            () => TestPaths.RequireExecutable("sk-missing", message, temp.Path));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_path_starts_the_committed_fake_streamlink()
    {
        var startInfo = new ProcessStartInfo(TestPaths.FixturePath("fake-streamlink"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("--version");

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("streamlink 0.0.0-fake", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Startable_script_runs_and_forwards_arguments_and_the_exit_code()
    {
        using var temp = new TempDirectory();
        string script = Path.Combine(temp.Path, "sk-script");
        File.WriteAllText(script, "#!/bin/sh\necho \"script:$1\"\nexit 7\n");

        var startInfo = new ProcessStartInfo(TestPaths.StartableScriptPath(script))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("hello");

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(7, process.ExitCode);
        Assert.Equal("script:hello", output.Trim());
    }
}
