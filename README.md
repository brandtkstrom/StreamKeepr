# StreamKeepr

StreamKeepr records Twitch streams to disk as HLS and plays them back in a Blazor web app.

## Running the real app

The app in `src/StreamKeepr` validates its configuration at startup, so the real recorder needs Twitch app credentials and a writable recordings root. For local development:

```bash
dotnet user-secrets --project src/StreamKeepr set "Twitch:ClientId" "<client-id>"
dotnet user-secrets --project src/StreamKeepr set "Twitch:ClientSecret" "<client-secret>"
dotnet user-secrets --project src/StreamKeepr set "Storage:RecordingsRoot" "$PWD/recordings"
dotnet run --project src/StreamKeepr
```

Then open <http://localhost:5080>.

## Mock profile (no Twitch credentials)

The `Mock` launch profile runs the whole app without Twitch, OAuth, or production data. It is a
development/E2E environment only: it registers fake admin auth, so never set
`ASPNETCORE_ENVIRONMENT=Mock` on a production host.

```bash
dotnet run --project src/StreamKeepr --launch-profile Mock
# or: dotnet watch --project src/StreamKeepr --launch-profile Mock
```

Open <http://localhost:5187>. The profile starts four fake channels and eight recordings:

- finished VODs, including two-part sessions and thumbnails;
- chapters derived from title/game changes;
- a growing EVENT capture recorded through `tests/fixtures/fake-live-streamlink`.

`MockPoller` supplies deterministic observations, `MockEventSubClient` accepts test-controlled
pushes, and the fake cookie auth signs in as `mock_admin` by default. None of these touch Twitch
or construct the real HTTP/EventSub clients. The startup tool preflight still runs, so
`ffmpeg`/`ffprobe` must be on `PATH`; Streamlink is always the committed fake fixture.

Mock data lives under the system temp directory (`.../streamkeepr-mock/recordings`), is reset
on each start, and is never read from user secrets. `Mock:RecordingsRoot` must be a directory
below the system temp directory; the host rejects any other path. A non-empty custom root
without the mock-seeded marker is refused rather than deleted. To keep the previous run:

```bash
Mock__Seed__ResetOnStart=false dotnet run --project src/StreamKeepr --launch-profile Mock
```

### Fake auth toggle

The fake auth is signed in by default. Launch signed out for E2E or manual checks:

```bash
Mock__Auth__SignedIn=false dotnet run --project src/StreamKeepr --launch-profile Mock
```

The same setting can be changed in `appsettings.Mock.json` under `Mock:Auth:SignedIn`. Signed-out
runs have no admin cookie; signing in through Twitch is not mapped in the Mock profile.

## Tests

```bash
dotnet build StreamKeepr.sln -warnaserror
dotnet format StreamKeepr.sln --verify-no-changes
dotnet test tests/StreamKeepr.Tests
dotnet test StreamKeepr.sln --filter "Category!=E2E"
```

`StreamKeepr.E2E` is the Playwright for .NET suite. It launches the Mock profile on
`http://127.0.0.1:5187`, waits for `/health/ready`, runs branded Chrome, and checks the shell with
axe. It needs ffmpeg and Google Chrome; install the Playwright browser dependencies first:

```bash
dotnet build tests/StreamKeepr.E2E
pwsh tests/StreamKeepr.E2E/bin/Debug/net10.0/playwright.ps1 install --with-deps chrome
dotnet test tests/StreamKeepr.E2E
```

Traces, screenshots, axe results, a small `report.html` index, and the Mock app log are written
to a unique directory under the system temp directory. Set `STREAMKEEPR_E2E_ARTIFACTS` to keep
them at a known path; CI sets it and uploads the directory as `playwright-results`. E2E has no
automatic retries; a failure is fixed or quarantined explicitly.

## Container image

The app ships as a non-root image (UID 1654) with Streamlink and static FFmpeg/ffprobe inside. It listens on port 8080 and checks `/health/live`.

```bash
docker build -t streamkeepr .
docker run --rm -p 8080:8080 \
  -v streamkeepr-recordings:/recordings \
  -e Twitch__ClientId="<client-id>" \
  -e Twitch__ClientSecret="<client-secret>" \
  streamkeepr
```

A named volume inherits the image's ownership. For a bind mount, run `sudo chown -R 1654:1654 <recordings>` first; the startup preflight fails with that instruction when the folder is not writable. Streamlink is pinned at 8.6.3 and can be bumped with `--build-arg STREAMLINK_VERSION=<version>`.

## HTTPS and the reverse proxy

Twitch sign-in requires HTTPS except on `localhost`. When TLS terminates at a reverse proxy, trust only that proxy (or network) so the app builds the public callback URL and secure cookie:

```json
"Hosting": {
  "ForwardedHeaders": {
    "KnownProxies": ["10.0.0.2"],
    "KnownNetworks": ["10.0.0.0/24"]
  }
}
```

Both lists default to empty, which ignores every `X-Forwarded-*` header. An app behind an unconfigured proxy refuses sign-in with `authError=httpsRequired` instead of sending Twitch a broken callback.

The old worker still builds beside the new app until the migration switch.
