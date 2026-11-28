using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using StreamKeepr.Configuration;
using StreamKeepr.Infrastructure.Storage;

namespace StreamKeepr.Hosting;

/// <summary>
/// Fails startup before any channel or recording service runs when a required
/// external tool, the recordings volume, or the record store's volume is unusable. This must be
/// the first hosted service registered by the host.
/// </summary>
public sealed class ToolPreflight(
    IOptions<RecorderOptions> recorderOptions,
    IOptions<StorageOptions> storageOptions,
    IDiskSpace disk,
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
        EnsureDatabaseVolume(storageOptions.Value);

        // The key ring lives in the database directory, so it is probed once that is writable.
        OwnerOnlyFileSystem.EnsureWritableKeyDirectory(
            AppDataPaths.DataProtectionKeysDirectory(storageOptions.Value.DatabasePath));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The record store's directory must be writable and keep its own free-space floor.</summary>
    private void EnsureDatabaseVolume(StorageOptions storage)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(storage.DatabasePath))!;
        try
        {
            OwnerOnlyFileSystem.EnsureWritableDirectory(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"Startup preflight failed: the database directory '{directory}' " +
                $"({StorageOptions.SectionName}:{nameof(StorageOptions.DatabasePath)}) is not writable. " +
                "Ensure the data volume is owned by the app user (UID 1654 in the container; " +
                "chown -R 1654:1654 <data>) or use a Docker named volume.",
                ex);
        }

        long freeBytes = disk.AvailableFreeSpace(directory);
        if (!DiskGuard.HasRoom(freeBytes, storage.DatabaseMinFreeGb))
        {
            throw new InvalidOperationException(
                $"Startup preflight failed: the database directory '{directory}' has " +
                $"{freeBytes / (double)DiskGuard.BytesPerGib:0.##} GB free, below " +
                $"{StorageOptions.SectionName}:{nameof(StorageOptions.DatabaseMinFreeGb)} " +
                $"({storage.DatabaseMinFreeGb} GB). Free space on the data volume or lower the setting.");
        }
    }

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
