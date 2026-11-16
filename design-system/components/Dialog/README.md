Confirmation dialogs for destructive actions: delete a recording, stop a recording, remove a channel.

## Build it from

Use `IDialogService.ShowMessageBoxAsync` for plain confirms, or a `MudDialog` component when the body needs an error banner or a busy state:

```razor
<MudDialog>
    <TitleContent><MudText Typo="Typo.h6">Delete recording?</MudText></TitleContent>
    <DialogContent>
        <MudAlert Severity="Severity.Error" Variant="Variant.Text" Dense="true">
            <b>@Recording.Title · @Recording.StreamerLogin</b><br />The files and watch progress are removed permanently.
        </MudAlert>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Variant="Variant.Filled" Color="Color.Error" Disabled="_deleting" OnClick="Confirm">Delete</MudButton>
    </DialogActions>
</MudDialog>
```

`DialogOptions`: `MaxWidth = MaxWidth.ExtraSmall`, `FullWidth = true`, `CloseOnEscapeKey = true`, and `BackdropClick = false` while busy.

## Rules

- The title names the thing as a question ("Stop recording for nightjar_tv?"). The body states the consequence in one or two sentences, in `body1`.
- Cancel (text, `Color.Default`) comes first, and the confirm (filled, `Color.Error`) is last and repeats the verb.
- While working, disable both buttons and set `aria-busy`. On failure, keep the dialog open and show the error in the banner (`role="alert"`).
- Ground `surface`, `radius-lg`, `elevation-24`, over `overlay-dark`.
