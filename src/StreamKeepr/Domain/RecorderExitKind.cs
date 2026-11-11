namespace StreamKeepr.Domain;

public enum RecorderExitKind
{
    /// <summary>The process ended on its own: stream end, network failure, or tool crash.</summary>
    Exited,

    /// <summary>The coordinator requested a graceful stop.</summary>
    Stopped,

    /// <summary>The watchdog or a force kill ended the process.</summary>
    Killed,
}
