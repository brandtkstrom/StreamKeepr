namespace StreamRecorder.Domain;

public sealed record RecordingTarget(
    string Directory,       // recordings/{Directory}
    string PlaylistPath,    // {Directory}/index.m3u8
    string SegmentPattern)  // {Directory}/segment_%05d.m4s
{
    public const string InitFileName = "init.mp4";
};