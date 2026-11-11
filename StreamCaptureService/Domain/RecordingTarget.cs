namespace StreamRecorder.Domain;

public sealed record RecordingTarget(
    string Directory,       // recordings/{Directory}
    string PlaylistPath)    // {Directory}/segment_%05d.m4s
{
    public const string SegmentPattern = "segment_%05d.m4s";
    public const string InitFileName = "init.mp4";
};
