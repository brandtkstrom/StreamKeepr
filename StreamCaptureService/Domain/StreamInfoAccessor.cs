namespace StreamRecorder.Domain;

public interface IStreamInfoAccessor
{
    StreamInfo? Get();
    void Set(StreamInfo? streamInfo);
}

public sealed class StreamInfoAccessor : IStreamInfoAccessor
{
    private StreamInfo? StreamInfo
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    public StreamInfo? Get() => StreamInfo;

    public void Set(StreamInfo? streamInfo)
    {
        StreamInfo = streamInfo;
    }
}
