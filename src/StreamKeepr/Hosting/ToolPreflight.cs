using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;

namespace StreamKeepr.Hosting;

/// <summary>
/// Fails startup before any channel or recording service runs when a required
/// external tool or the recordings volume is unusable. This must be the first
/// hosted service registered by the host.
/// </summary>
public sealed class ToolPreflight(
    IOptions<RecorderOptions> recorderOptions,
    IOptions<StorageOptions> storageOptions,
    ILogger<ToolPreflight> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        RecorderOptions recorder = recorderOptions.Value;

        await LogVersionAsync(
            "ffmpeg",
            recorder.FfmpegPath,
            "-version",
            $"{RecorderOptions.SectionName}:{nameof(RecorderOptions.FfmpegPath)}",
            cancellationToken);
        await LogVersionAsync(
            "ffprobe",
            recorder.FfprobePath,
            "-version",
            $"{RecorderOptions.SectionName}:{nameof(RecorderOptions.FfprobePath)}",
            cancellationToken);
        await LogVersionAsync(
            "streamlink",
            recorder.StreamlinkPath,
            "--version",
            $"{RecorderOptions.SectionName}:{nameof(RecorderOptions.StreamlinkPath)}",
            cancellationToken);

        string recordingsRoot = storageOptions.Value.RecordingsRoot;
        OwnerOnlyFileSystem.EnsureWritableRecordingsRoot(recordingsRoot);
        OwnerOnlyFileSystem.EnsureWritableKeyDirectory(AppDataPaths.KeysDirectory(recordingsRoot));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task LogVersionAsync(
        string tool,
        string path,
        string versionArgument,
        string setting,
        CancellationToken cancellationToken)
    {
        BufferedCommandResult result;
        try
        {
            result = await Cli.Wrap(path)
                .WithArguments(versionArgument)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Startup preflight failed: the required tool '{tool}' could not be run from '{path}'. " +
                $"Install {tool} or set {setting} to its executable path. {ex.Message}",
                ex);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Startup preflight failed: '{path}' exited with code {result.ExitCode} for " +
                $"'{versionArgument}'. Install a working {tool} or set {setting} to its executable path. " +
                FirstLine(result.StandardError));
        }

        logger.LogInformation("{Tool}: {Version}", tool, FirstLine(result.StandardOutput));
    }

    private static string FirstLine(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                return line.Trim();
        }

        return "(no version output)";
    }
}
