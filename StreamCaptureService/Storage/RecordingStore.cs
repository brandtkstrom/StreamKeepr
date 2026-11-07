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
    public RecordingTarget CreateTarget(StreamInfo streamInfo)
    {
        // TODO - sanitize stream title, update record paths
        var recordPath = Path.Join(config.RecorderOptions.OutputDirectory,
                                   config.TwitchOptions.UserLogin,
                                   $"{DateTime.Now:s}_{streamInfo.StreamId}");

        var playlistPath = Path.Join(recordPath, "index.m3u8");

        return new RecordingTarget(recordPath, playlistPath);
    }

    public bool Writable()
    {
        try
        {
            // Create a file with a random name, ensuring it's deleted on close.
            var testPath = Path.Combine(config.RecorderOptions.OutputDirectory, Path.GetRandomFileName());
            using var _ = File.Create(testPath, 1, FileOptions.DeleteOnClose);

            return true;
        }
        catch
        {
            return false;
        }
    }
}