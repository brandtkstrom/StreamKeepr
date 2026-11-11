using System.IO.Pipelines;
using CliWrap;
using CliWrap.Builders;
using StreamRecorder.Config;
using StreamRecorder.Domain;

namespace StreamRecorder.Recording;

/// <summary>
/// Process outcome state.
/// </summary>
/// <param name="ExitCode">Process exit code</param>
/// <param name="Error">Error thrown by process</param>
public abstract record ProcessResult(int? ExitCode, Exception? Error)
{
    public record Exited(int? ExitCode) : ProcessResult(ExitCode, null);
    public record Cancelled() : ProcessResult(null, null);
    public record Faulted(Exception Error) : ProcessResult(null, Error);

    public static Exited WithCode(int ExitCode) => new(ExitCode);
    public static Faulted WithError(Exception Error) => new(Error);
    public static readonly Cancelled ProcessCancelled = new();

    public bool HasError => (ExitCode.HasValue && ExitCode != 0)
                            || Error is not null;
}

public interface IStreamCapturePipeline
{
    public Task<RecordingResult> StartCaptureAsync(RecordingTarget recordingTarget,
                                                   CancellationToken cancelToken);

    Task StopAsync();
}

public sealed class StreamCapturePipeline : IStreamCapturePipeline
{
    private CancellationTokenSource CTS = new();

    private readonly AppConfig _appConfig;
    private readonly ILogger<StreamCapturePipeline> _logger;

    public StreamCapturePipeline(AppConfig appConfig, ILogger<StreamCapturePipeline> logger)
    {
        _appConfig = appConfig;
        _logger = logger;
    }

    // Time ffmpeg gets to flush the final segment after its input hits EOF.
    // After this it is hard-killed. There is deliberately no SIGINT step: a hard kill
    // leaves the last good playlist in place, while an extra interrupt signal can make
    // ffmpeg rename an empty playlist over the good one.
    private static readonly TimeSpan FinalizeGraceTime = TimeSpan.FromSeconds(30);

    public async Task<RecordingResult> StartCaptureAsync(RecordingTarget recordingTarget,
                                                         CancellationToken cancelToken)
    {
        var outputDir = recordingTarget.Directory;
        var playlistPath = recordingTarget.PlaylistPath;
        var segmentsPath = Path.Join(outputDir, "segments");

        Directory.CreateDirectory(segmentsPath);

        var streamlinkPath = _appConfig.RecorderOptions.StreamlinkPath;
        var ffmpegPath = _appConfig.RecorderOptions.FfmpegPath;

        // streamlink process writes raw HLS segments to stdout
        var streamlink = WrapProcess("streamlink", streamlinkPath, BuildStreamlinkArgs);
        // ffmpeg processes input from stdin
        var ffmpeg = WrapProcess("ffmpeg", ffmpegPath, BuildFfmpegArgs);

        // Start capture pipeline
        var (streamlinkResult, ffmpegResult) = await StartCaptureAsync(streamlink, ffmpeg, cancelToken);

        // ffmpeg is guaranteed to be gone at this point, so nothing can overwrite our repair.
        var finalized = await EnsurePlaylistEndedAsync(playlistPath);

        if (!finalized)
        {
            _logger.LogWarning("failed to finalize playlist: {PlaylistPath}", playlistPath);
        }

        return MapRecordingResult(streamlinkResult, ffmpegResult, playlistPath);

        Command WrapProcess(string name, string exePath, Action<ArgumentsBuilder> argsBuilder)
        {
            var errorPipe = _logger.IsEnabled(LogLevel.Information)
                                ? PipeTarget.ToDelegate(line => _logger.LogInformation("[{Name}] {Line}", name, line))
                                : PipeTarget.Null;

            return Cli.Wrap(exePath)
                      .WithWorkingDirectory(outputDir)
                      .WithArguments(argsBuilder)
                      .WithStandardErrorPipe(errorPipe)
                      .WithValidation(CommandResultValidation.None);
        }

        void BuildStreamlinkArgs(ArgumentsBuilder args)
        {
            args.Add("--stdout")
                .Add("--loglevel").Add("error")
                .Add("--retry-max").Add("20")
                .Add("--retry-open").Add("10")
                .Add("--retry-streams").Add("15")
                .Add("--stream-segment-threads").Add("4")
                .Add("--stream-segment-attempts").Add("10")
                .Add("--stream-segment-timeout").Add("20")
                .Add("--stream-timeout").Add("120")
                .Add("--hls-playlist-reload-attempts").Add("5")
                .Add("--hls-live-edge").Add("6")
                .Add("--hls-segment-stream-data")
                .Add("--twitch-supported-codecs").Add("h264,h265,av1")
                .Add("--ringbuffer-size").Add("256M");

            if (_appConfig.TwitchOptions.UserAuthToken is { Length: >= 20 and <= 64 } authToken)
            {
                args.Add("--twitch-api-header").Add($"Authorization=OAuth {authToken}");
            }

            args.Add($"twitch.tv/{_appConfig.TwitchOptions.UserLogin}")
                .Add(_appConfig.RecorderOptions.Quality);
        }

        void BuildFfmpegArgs(ArgumentsBuilder args)
        {
            args.Add("-hide_banner").Add("-y") //.Add("-stats")
                .Add("-loglevel").Add("warning")
                .Add("-fflags").Add("+genpts+igndts+discardcorrupt")
                .Add("-err_detect").Add("ignore_err")
                .Add("-bsf:a").Add("aac_adtstoasc")
                .Add("-avoid_negative_ts").Add("make_zero")
                .Add("-i").Add("pipe:0")
                .Add("-c").Add("copy")
                .Add("-f").Add("hls")
                .Add("-hls_segment_type").Add("fmp4")
                .Add("-hls_time").Add(_appConfig.RecorderOptions.HlsSegmentSeconds)
                .Add("-hls_list_size").Add("0")
                .Add("-hls_playlist_type").Add("event")
                .Add("-hls_flags").Add("append_list+independent_segments+program_date_time+temp_file+discont_start")
                .Add("-hls_base_url").Add("segments/")
                .Add("-hls_fmp4_init_filename").Add($"segments/{RecordingTarget.InitFileName}")
                .Add("-hls_segment_filename").Add($"segments/{RecordingTarget.SegmentPattern}")
                .Add(playlistPath);
        }
    }

    public Task StopAsync() => CTS.CancelAsync();

    private static RecordingResult MapRecordingResult(ProcessResult StreamlinkResult,
                                                      ProcessResult FfmpegResult,
                                                      string playlistPath)
    {
        RecordingOutcome outcome = RecordingOutcome.StreamEnded;

        if (StreamlinkResult is ProcessResult.Cancelled
            || FfmpegResult is ProcessResult.Cancelled)
        {
            outcome = RecordingOutcome.Cancelled;
        }
        else if (StreamlinkResult.HasError || FfmpegResult.HasError)
        {
            outcome = RecordingOutcome.Failed;
        }

        return new RecordingResult(outcome, playlistPath);
    }

    private async Task<(ProcessResult StreamlinkResult, ProcessResult FfmpegResult)> StartCaptureAsync(
        Command streamlinkCommand, Command ffmpegCommand, CancellationToken cancelToken)
    {
        // In-process pipe between the two processes. It provides backpressure, and it
        // lets us decide exactly when ffmpeg's stdin reaches EOF.
        var pipe = new Pipe();
        var streamlink = streamlinkCommand.WithStandardOutputPipe(PipeTarget.ToStream(pipe.Writer.AsStream()));
        var ffmpeg = ffmpegCommand.WithStandardInputPipe(PipeSource.FromStream(pipe.Reader.AsStream()));

        // streamlink is stopped by the caller's stop request, or by us if ffmpeg dies.
        // using var streamlinkCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        using var streamlinkCts = CancellationTokenSource.CreateLinkedTokenSource(CTS.Token, cancelToken);

        // ffmpeg is deliberately NOT linked to stopToken, and never gets a graceful token.
        // We stop it by ending its input; this token is only a last-resort hard kill if it hangs.
        using var ffmpegKillCts = new CancellationTokenSource();

        // Start ffmpeg first so it is already reading when data arrives.
        var ffmpegTask = ExecuteAsync(ffmpeg, ffmpegKillCts.Token);
        var streamlinkTask = RunStreamlinkAsync(streamlinkCts.Token);

        var first = await Task.WhenAny(ffmpegTask, streamlinkTask);

        var readerCompleted = false;

        if (first == ffmpegTask)
        {
            // ffmpeg ended (or failed to start) while streamlink is still producing. CliWrap has
            // already stopped feeding ffmpeg's stdin, so completing the reader is safe. It unblocks
            // any writer waiting on backpressure; then streamlink is killed.
            _logger.LogError("ffmpeg exited before streamlink; stopping streamlink.");
            await pipe.Reader.CompleteAsync();
            readerCompleted = true;
            await streamlinkCts.CancelAsync();
        }

        var streamlinkTaskResult = await streamlinkTask;
        LogProcessResult("streamlink", streamlinkTaskResult);

        // ffmpeg now has EOF and should finish on its own. If it hangs, hard-kill it.
        ffmpegKillCts.CancelAfter(FinalizeGraceTime);

        var ffmpegTaskResult = await ffmpegTask;
        LogProcessResult("ffmpeg", ffmpegTaskResult);

        if (!readerCompleted)
        {
            await pipe.Reader.CompleteAsync();
        }

        return (streamlinkTaskResult, ffmpegTaskResult);

        async Task<ProcessResult> RunStreamlinkAsync(CancellationToken ct)
        {
            try
            {
                return await ExecuteAsync(streamlink, ct);
            }
            finally
            {
                // Always close the writer so ffmpeg sees EOF and finalizes the playlist.
                await pipe.Writer.CompleteAsync();
            }
        }
    }

    /// <summary>
    /// Runs a command and captures exit code or error
    /// </summary>
    private static async Task<ProcessResult> ExecuteAsync(Command command, CancellationToken forceful)
    {
        try
        {
            var result = await command.ExecuteAsync(forceful, CancellationToken.None);
            return ProcessResult.WithCode(result.ExitCode);
        }
        catch (OperationCanceledException)
        {
            return ProcessResult.ProcessCancelled;
        }
        catch (Exception ex)
        {
            return ProcessResult.WithError(ex);
        }
    }

    private void LogProcessResult(string name, ProcessResult result)
    {
        switch (result)
        {
            case ProcessResult.Faulted { Error: not null } faulted:
                _logger.LogError(faulted.Error, "{Process} failed", name);
                break;
            case ProcessResult.Cancelled:
                _logger.LogInformation("{Process} cancelled", name);
                break;
            case ProcessResult.Exited:
                _logger.LogInformation("{Process} exited: {Code}", name, result.ExitCode?.ToString() ?? "killed");
                break;
            default:
                throw new NotImplementedException($"Unhandled process result '{result.GetType().Name}': {result}");
        }
    }

    /// <summary>
    /// If ffmpeg crashed or was killed, #EXT-X-ENDLIST may be missing. The segments already
    /// listed are complete (thanks to temp_file), so appending the tag turns the playlist
    /// back into a valid, finished recording. Returns true if ffmpeg had finalized it itself.
    /// Must only be called once ffmpeg has exited. Never throws.
    /// </summary>
    private async Task<bool> EnsurePlaylistEndedAsync(string playlistPath)
    {
        try
        {
            if (!File.Exists(playlistPath))
            {
                _logger.LogWarning("No playlist was produced at {Path}", playlistPath);
                return false;
            }

            var text = await File.ReadAllTextAsync(playlistPath);

            // An empty or damaged playlist (like the empty-rename case) can't be fixed by adding
            // a tag, and writing to it would only make things worse. Leave it for manual recovery.
            if (!text.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                _logger.LogError("Playlist {Path} is empty or damaged; leaving it untouched.", playlistPath);
                return false;
            }

            if (text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                return true;
            }

            _logger.LogWarning("Playlist was not finalized by ffmpeg; appending #EXT-X-ENDLIST.");

            // Write the repaired playlist atomically, the same way ffmpeg's temp_file does,
            // so a crash during the repair can't leave a half-written playlist.
            var repaired = text + (text.EndsWith('\n') ? "" : "\n") + "#EXT-X-ENDLIST\n";
            var tempPath = playlistPath + ".repair.tmp";
            await File.WriteAllTextAsync(tempPath, repaired);
            File.Move(tempPath, playlistPath, overwrite: true);

            // Any leftover *.tmp segment is a partial write from the crash; ignore or delete it.
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The recording itself is fine; only the repair failed. Don't let it mask the capture's outcome.
            _logger.LogError(ex, "Could not check or repair playlist {Path}", playlistPath);
        }

        return false;
    }
}
