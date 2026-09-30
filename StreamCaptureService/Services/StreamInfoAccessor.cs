using LanguageExt;
using StreamRecorder.Core;

namespace StreamRecorder.Services;

public sealed class StreamInfoAccessor
{
    private StreamInfo? StreamInfo
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    public Option<StreamInfo> Get() => StreamInfo;

    public void Set(Option<StreamInfo> streamInfo)
    {
        StreamInfo = streamInfo.IfNoneUnsafe((StreamInfo?) null);
    }
}