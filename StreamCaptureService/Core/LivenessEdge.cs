using LanguageExt;
using static LanguageExt.Prelude;

namespace StreamRecorder.Core;

/// <summary>
/// Pure edge detection for the poll loop: given what the last poll saw, what this poll
/// saw, and where the recorder currently is, decides which event (if any) to raise.
/// Extracted from <c>TwitchMonitorService</c> so the retry-after-fault rule is testable
/// without a Twitch client.
/// </summary>
public static class LivenessEdge
{
    /// <summary>
    /// Pure function. Given identical inputs, always returns identical output.
    /// The returned <c>WasLive</c> is the flag to carry into the next poll.
    /// </summary>
    public static (bool WasLive, Option<RecorderEvent> Event) Detect(
        bool wasLive,
        bool isLive,
        RecorderState recorderState)
    {
        return (isLive, wasLive) switch
        {
            // Stream came online.
            (true, false) =>
                (true, Some<RecorderEvent>(new RecorderEvent.StreamWentLive())),
            // Stream went offline.
            (false, true) =>
                (false, Some<RecorderEvent>(new RecorderEvent.StreamWentOffline())),
            // Still live but nothing is recording: re-raise StreamWentLive on every poll so
            // the transition can retry or resume. Faulted covers a pipeline that died
            // mid-stream, Interrupted a clean exit, and Offline a session that has not started
            // or has just ended. The transition owns the failure budget and declines the event
            // once it is exhausted — no side effects, and the transition log line each poll
            // shows the event was declined.
            (true, _) when recorderState is RecorderState.Faulted or RecorderState.Interrupted or RecorderState.Offline =>
                (true, Some<RecorderEvent>(new RecorderEvent.StreamWentLive())),
            // Still offline but parked in Faulted or Interrupted: the one-shot offline edge may
            // have been consumed while Starting or Stopping, so re-raise it until the failure
            // budget is reset and the session is closed. Without this, a fault in the previous
            // broadcast can carry its budget and session into the next one.
            (false, _) when recorderState is RecorderState.Faulted or RecorderState.Interrupted =>
                (false, Some<RecorderEvent>(new RecorderEvent.StreamWentOffline())),
            // Default
            var _ => (wasLive, None)
        };
    }
}