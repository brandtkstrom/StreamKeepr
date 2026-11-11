using System.Globalization;
using StreamRecorder.Config;
using StreamRecorder.Domain;

namespace StreamRecorder.Storage;

public interface IRecordingStore
{
    RecordingTarget CreateTarget(StreamInfo streamInfo);
    bool Writable();
}

public class RecordingStore(AppConfig config) : IRecordingStore
{
    private string OutputBaseDir => Path.GetFullPath(config.RecorderOptions.OutputDirectory);

    public RecordingTarget CreateTarget(StreamInfo streamInfo)
    {
        var timestamp = streamInfo.StartedAt.LocalDateTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

        var recordPath = Path.Join(OutputBaseDir,
                                   config.TwitchOptions.UserLogin,
                                   $"{timestamp}_{streamInfo.StreamId}");

        var playlistPath = Path.Join(recordPath, $"{streamInfo.Title.Sanitize()}.m3u8");

        return new RecordingTarget(recordPath, playlistPath);
    }

    public bool Writable()
    {
        try
        {
            // Create a file with a random name, ensuring it's deleted on close.
            var testPath = Path.Combine(OutputBaseDir, Path.GetRandomFileName());
            using var _ = File.Create(testPath, 1, FileOptions.DeleteOnClose);

            return true;
        }
        catch
        {
            // suppress errors
        }

        return false;
    }
}
