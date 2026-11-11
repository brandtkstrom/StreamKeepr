using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace StreamKeepr.E2E;

/// <summary>
/// Publishes the app once, starts the published host with the <c>Mock</c> environment on a free
/// loopback port, waits for <c>/health/ready</c>, and keeps its logs for a failure report. The
/// recordings root is a unique temp directory so the suite never reads or writes a developer's or
/// production library.
///
/// The suite runs the published output rather than <c>dotnet run</c>: the SDK's static-web-asset
/// development handler resolves the framework <c>_framework/*.js</c> files against the source
/// <c>wwwroot</c>, where they do not exist, so the interactive circuit never starts. Publish
/// copies the framework assets into the output webroot and serves them correctly.
/// </summary>
public sealed class MockAppFixture : IAsyncLifetime
{
    public string BaseUrl { get; } = $"http://127.0.0.1:{FindFreePort()}";

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(300);

    private readonly ConcurrentQueue<string> _logs = new();
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "streamkeepr-e2e", Guid.NewGuid().ToString("N"));
    private Process? _process;
    private string? _logPath;

    public string RepositoryRoot { get; } = FindRepositoryRoot();

    public string ArtifactsDirectory { get; } = ResolveArtifactsDirectory();

    private string RecordingsRoot => Path.Combine(_tempRoot, "recordings");

    private string PublishRoot => Path.Combine(_tempRoot, "publish");

    public async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(ArtifactsDirectory);
            Directory.CreateDirectory(RecordingsRoot);
            _logPath = Path.Combine(ArtifactsDirectory, "mock-app.log");
            File.Delete(_logPath);

            await PublishAsync();

            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            startInfo.ArgumentList.Add(Path.Combine(PublishRoot, "StreamKeepr.dll"));
            startInfo.ArgumentList.Add("--urls");
            startInfo.ArgumentList.Add(BaseUrl);
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Mock";
            startInfo.Environment["ASPNETCORE_CONTENTROOT"] =
                Path.Combine(RepositoryRoot, "src", "StreamKeepr");
            startInfo.Environment["ASPNETCORE_WEBROOT"] =
                Path.Combine(PublishRoot, "wwwroot");
            startInfo.Environment["Mock__RecordingsRoot"] = RecordingsRoot;
            startInfo.Environment["Mock__Seed__ResetOnStart"] = "true";

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, args) => Capture(args.Data);
            _process.ErrorDataReceived += (_, args) => Capture(args.Data);

            if (!_process.Start())
                throw new InvalidOperationException("Could not start the Mock profile.");

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            await WaitForReadyAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                // Best effort teardown; the temp root is disposable even when a child lingers.
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);

        WriteReport();
    }

    private async Task PublishAsync()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add(Path.Combine("src", "StreamKeepr", "StreamKeepr.csproj"));
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Debug");
        startInfo.ArgumentList.Add("-warnaserror");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(PublishRoot);
        startInfo.ArgumentList.Add("-nologo");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Could not publish the app for the E2E suite.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(PublishTimeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"The E2E publish did not finish within {PublishTimeout.TotalSeconds:N0} s.");
        }

        string output = $"{await stdout}{Environment.NewLine}{await stderr}";
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The E2E publish failed with exit code {process.ExitCode}.{Environment.NewLine}{output}");
        }
    }

    private async Task WaitForReadyAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ReadyTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process?.HasExited == true)
            {
                throw new InvalidOperationException(
                    $"The Mock profile exited with code {_process.ExitCode} before becoming ready.\n" +
                    Logs());
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync($"{BaseUrl}/health/ready");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // The host is still starting.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"The Mock profile did not become ready at {BaseUrl}/health/ready within " +
            $"{ReadyTimeout.TotalSeconds:N0} s.\n{Logs()}");
    }

    private void Capture(string? line)
    {
        if (line is null)
            return;

        _logs.Enqueue(line);
        if (_logPath is not null)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never take the suite down.
            }
        }
    }

    private string Logs() => string.Join(Environment.NewLine, _logs);

    /// <summary>
    /// Writes a small index over every collected artifact so CI uploads one browsable report
    /// with the traces, screenshots, axe output, and app log. Pass/fail detail stays in the
    /// TRX produced by <c>dotnet test</c>.
    /// </summary>
    private void WriteReport()
    {
        if (!Directory.Exists(ArtifactsDirectory))
            return;

        string reportPath = Path.Combine(ArtifactsDirectory, "report.html");
        string[] links = Directory
            .EnumerateFiles(ArtifactsDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, reportPath, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                string relative = Path.GetRelativePath(ArtifactsDirectory, path);
                string encoded = WebUtility.HtmlEncode(relative);
                return $"<li><a href=\"{encoded}\">{encoded}</a></li>";
            })
            .ToArray();

        string html = $"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>StreamKeepr E2E artifacts</title>
            </head>
            <body>
              <h1>StreamKeepr E2E artifacts</h1>
              <p>Open a trace with <code>npx playwright show-trace &lt;file.zip&gt;</code>.
                 Pass/fail detail is in the TRX produced by <code>dotnet test</code>.</p>
              <ul>
            {string.Join(Environment.NewLine, links)}
              </ul>
            </body>
            </html>
            """;

        File.WriteAllText(reportPath, html);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string ResolveArtifactsDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("STREAMKEEPR_E2E_ARTIFACTS");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "streamkeepr-e2e", Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(configured);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "StreamKeepr.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate StreamKeepr.sln above the E2E test output directory.");
    }
}

[CollectionDefinition(Name)]
public sealed class MockAppCollection : ICollectionFixture<MockAppFixture>
{
    public const string Name = "MockApp";
}
