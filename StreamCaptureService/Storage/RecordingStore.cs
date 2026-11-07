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
        // TODO - check dest path write perms
        return true;
    }
}