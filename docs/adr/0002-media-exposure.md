# Media exposure is an allowlisted surface

## Status

Accepted

## Context

`/media` serves recording files. Serving the recordings root as general static files exposes `session.json` and `.streamkeepr`: known MIME types are served even when `ServeUnknownFileTypes` is false, and dot-prefixed segments are not excluded by default.

## Decision

`/media` serves only `.m3u8`, `.m4s`, `.mp4`, `.jpg`, and `.png`. Any request with a dot-prefixed path segment or a non-allowlisted extension returns 404. `session.json`, `.streamkeepr`, temp files, and exports are never served. The live `index.m3u8` remains an endpoint; segmented media uses the static provider.

## Consequences

- A new media type requires an explicit allowlist change.
- Integration tests must assert 404 for `.json` and dot-folders.
- Exports and progress data live outside the served extension set.
