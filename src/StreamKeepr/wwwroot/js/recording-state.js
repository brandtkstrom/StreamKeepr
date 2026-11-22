// The islands' one copy of the "still being recorded" rule. It mirrors
// RecordingStateExtensions.IsOpen (Ui/LibraryViewModels.cs) for the state names the
// server sends to JS ("recording", "reconnecting", "saved", ...).

/** True while the recording is still being captured, including a reconnect. */
export function isLiveState(state) {
  return state === "recording" || state === "reconnecting";
}
