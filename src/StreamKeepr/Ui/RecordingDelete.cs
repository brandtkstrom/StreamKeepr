namespace StreamKeepr.Ui;

/// <summary>
/// When a recording can be deleted, shared by the title block and the library row (design
/// system: RecordingDetails, RecordingRow). The server enforces the same rules.
/// </summary>
public static class RecordingDelete
{
    public const string WhileLive = "Available when the recording finishes";
    public const string WhileExporting = "Cancel the export before deleting";
    public const string SignedOut = "Connect Twitch to use this";

    /// <summary>Why Delete is disabled, or null when it is available.</summary>
    public static string? BlockedReason(RecordingView recording, bool exportActive) =>
        recording.State is RecordingState.Recording or RecordingState.Reconnecting ? WhileLive
        : exportActive ? WhileExporting
        : null;
}
