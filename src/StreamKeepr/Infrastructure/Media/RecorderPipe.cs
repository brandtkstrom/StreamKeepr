using System.IO.Pipelines;

namespace StreamKeepr.Infrastructure.Media;

/// <summary>
/// Creates the bounded bridge between Streamlink's stdout and FFmpeg's stdin. The writer pauses
/// at four MiB and resumes at two MiB, so a slow FFmpeg backpressures Streamlink instead of
/// buffering the stream without limit.
/// </summary>
public static class RecorderPipe
{
    public const int PauseWriterThreshold = 4 << 20;
    public const int ResumeWriterThreshold = 2 << 20;

    public static Pipe Create() => new(new PipeOptions(
        pauseWriterThreshold: PauseWriterThreshold,
        resumeWriterThreshold: ResumeWriterThreshold));
}
