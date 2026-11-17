using System.Diagnostics;

namespace StreamKeepr.Tests.Hosting;

/// <summary>
/// Runs the built app as a real process, because the fatal log, the flush, and the
/// exit code only exist outside <c>WebApplicationFactory</c>.
/// </summary>
public sealed class FatalStartupProcessTests
{
    private const string SecretValue = "fatal-test-secret-do-not-log";
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData("Twitch__ClientId", "", "ClientId")]
    [InlineData("Serilog__MinimumLevel__Default", "Loud", "Loud")]
    public async Task Bad_configuration_logs_a_fatal_line_and_exits_non_zero(
        string key, string value, string expectedDetail)
    {
        string recordings = Path.Combine(
            Path.GetTempPath(), "streamkeepr-tests", Guid.NewGuid().ToString("N"));

        try
        {
            (int exitCode, string output) = await RunAppAsync(new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
                ["Twitch__ClientId"] = "fatal-test-client-id",
                ["Twitch__ClientSecret"] = SecretValue,
                ["Storage__RecordingsRoot"] = recordings,
                [key] = value,
            });

            Assert.NotEqual(0, exitCode);
            Assert.Contains("FTL]", output, StringComparison.Ordinal);
            Assert.Contains("Stream Keepr terminated unexpectedly", output, StringComparison.Ordinal);
            Assert.Contains(expectedDetail, output, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(recordings))
                Directory.Delete(recordings, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAppAsync(
        IReadOnlyDictionary<string, string> environment)
    {
        string appDirectory = AppContext.BaseDirectory;
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { Path.Combine(appDirectory, "StreamKeepr.dll") },
            WorkingDirectory = appDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var (key, value) in environment)
            start.Environment[key] = value;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the app process.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ExitTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The app did not exit within {ExitTimeout}.");
        }

        return (process.ExitCode, await stdout + await stderr);
    }
}
