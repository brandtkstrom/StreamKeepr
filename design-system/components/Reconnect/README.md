What the viewer sees when the server circuit is lost for good: rejoin failed, session paused, or resume failed.

## Build it from

The Blazor framework owns this dialog (`<dialog id="components-reconnect-modal">` in `ReconnectModal.razor`, driven by `reconnect.js`). It has to work while the circuit is down, so **it cannot be a MudBlazor component**. Keep the plain `<dialog>`, and style it like a `MudDialog`:

- `surface` ground, `radius-lg`, `elevation-24`, over `overlay-dark` (`dialog::backdrop`); centred, about 360px wide.
- A 32px icon: `WifiOff` in `warning` for rejoin failed, `Pause` in `text-secondary` for paused, `Error` in `error` for resume failed. Copy the SVG from `Icons.Material.Rounded`, since `MudIcon` won't render without the circuit.
- The title in `h6`, one `body1` sentence in `text-secondary`, then the actions: a text "Reload" and a filled "Retry" or "Resume".

| State | Title | Body | Actions |
|---|---|---|---|
| `failed` | Couldn’t rejoin the server | Retry now, or reload the page. StreamKeepr also retries when you come back to this tab. | Reload · Retry |
| `paused` | Session paused | The server paused this session. Resume to pick up where you left off. | Resume |
| `resume-failed` | Couldn’t resume the session | Try again, or reload the page to start a new session. | Reload · Resume |

## Rules

- A brief reconnect (`show`, `retrying`) never raises this dialog. The drawer's "Live updates paused" alert covers it, and playback keeps going (see `Feedback`).
- `rejected` reloads the page without showing anything.
- The copy above replaces the template's "Rejoin failed... trying again" text, with the same meaning in StreamKeepr's voice. The Reload button is new: today, reloading is only mentioned in the text.
