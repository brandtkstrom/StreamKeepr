# StreamKeepr

StreamKeepr records Twitch streams to disk as HLS and plays them back in a Blazor web app.

## Project scope

StreamKeepr is a personal project intended for private home use. It is not designed, tested, or supported for public hosting, multi-tenant use, or third-party distribution. The MIT license permits reuse, but the project's intent and support scope are private. Do not expose it to the public internet without your own hardening.

The deployment guidance in this repository assumes a trusted operator and a private home or LAN environment. See [`SECURITY.md`](SECURITY.md) and [`deploy/README.md`](deploy/README.md).

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

## Compose deployment

To run a released image without the source, use [`deploy/`](deploy/README.md). It has an example `compose.yaml` for `ghcr.io/brandtkstrom/streamkeepr` with every setting documented, a `.env` template, and empty secret templates.

The repository's own `compose.yaml` builds the non-root image (UID 1654) and runs one `stream-keepr` service with Streamlink and static FFmpeg/ffprobe inside. It publishes plain HTTP on port 8080, stores recordings on a named volume, enables `init`, inherits the image's `/health/live` healthcheck, rotates Docker logs, and gives shutdown a 50 s grace period.

### Secret files and permissions

Create the secret files from the checked-in examples, from the repository root:

```bash
cp secrets/twitch_client_id.txt.example secrets/twitch_client_id.txt
cp secrets/twitch_client_secret.txt.example secrets/twitch_client_secret.txt
# Edit both files and replace the placeholders.
```

Docker Compose mounts these files into `/run/secrets`. That is a bind mount on Linux, so the container process (UID 1654) must be able to read the host files. Make UID 1654 the owner and remove group/other access:

```bash
sudo chown 1654:1654 secrets/twitch_client_id.txt secrets/twitch_client_secret.txt
sudo chmod 400 secrets/twitch_client_id.txt secrets/twitch_client_secret.txt
```

Mode `600` with owner `1654:1654` also works. Do not make real secrets world-readable or add them to the image, and never commit them. On macOS Docker Desktop, file ownership is mapped by the VM; the `chown` is harmless but usually unnecessary.

`secrets/.gitignore` keeps the `*.example` files and ignores the real files.

Start the service:

```bash
docker compose up --build --detach
docker compose logs --follow stream-keepr
```

Open <http://localhost:8080>. The image runs as UID 1654 and reads recordings from `/recordings`.

The service reads secret files from `/run/secrets`; `AddKeyPerFile` maps `__` in a file name to `:` in the configuration key, so `Twitch__ClientId` becomes `Twitch:ClientId`. The secrets are not environment variables and are never baked into the image. To use a subscriber-only Streamlink token, create `secrets/recorder_twitch_user_oauth_token.txt`, apply the same ownership and mode, and uncomment both `Recorder__TwitchUserOAuthToken` entries in `compose.yaml`.

To seed the first channel on a fresh deployment, uncomment `Twitch__Channels__0` in `compose.yaml` and set it to a Twitch login, or leave `${TWITCH_CHANNEL_LOGIN}` and set that variable in `.env`. Do not leave an empty value: an empty login fails options validation. The seeded login is only used when the persisted channel list is first created; later changes are made through the application's channel controls.

### Volume ownership

A named volume inherits the image's ownership of `/recordings`. To use a host folder instead, replace the volume with `- ./recordings:/recordings` and run:

```bash
sudo chown -R 1654:1654 ./recordings
```

The startup preflight fails with this instruction when the root is not writable.

### HTTPS reverse proxy

Sign-in requires HTTPS except on `localhost`, so plain port 8080 is for viewing and local administration only. Put a TLS-terminating reverse proxy in front of the service and trust only that proxy (or its network):

```json
"Hosting": {
  "ForwardedHeaders": {
    "KnownProxies": ["10.0.0.2"],
    "KnownNetworks": ["10.0.0.0/24"]
  }
}
```

Both lists default to empty, which ignores every `X-Forwarded-*` header. An app behind an unconfigured proxy refuses sign-in with `authError=httpsRequired` instead of sending Twitch a broken callback.

### Stop, restart, and logs

`docker compose stop` sends SIGTERM through `init` to the app. Active recordings finalize within the host's 40 s shutdown budget, below Compose's 50 s grace period. `docker compose restart` (or `start`) recovers unfinished sessions on the next launch. Data Protection keys live under `/recordings/.streamkeepr/keys`, so sign-in cookies survive a restart. `docker compose down` keeps the recordings volume; `docker compose down --volumes` deletes recordings and keys.

Docker log rotation is capped at 10 MB per file with five files.

### Compose smoke test

The smoke test builds an isolated image and first checks the production contract: health, non-root UID, volume ownership, secret availability as UID 1654, log rotation, clean stop, and restart. It then starts the test-only Mock override with `./tests/fixtures` mounted read-only, records the growing Mock capture through the fake Streamlink, stops the service, and verifies every part playlist has exactly one `ENDLIST`. It restarts the same container to prove the `sk-auth` cookie and Data Protection keys survive and a seeded deep link still renders.

```bash
./scripts/compose-smoke.sh
```

The smoke test uses a unique Compose project, container, image tag, and temporary secret directory. It never touches an operator's stack, recordings, or secrets.

## Container image

The image can also be run directly. A named volume inherits the image's ownership; a bind mount needs the `chown` above. Mount secret files rather than passing credentials in `docker run --env` arguments:

```bash
docker build -t streamkeepr .
docker run --rm -p 8080:8080 \
  -v streamkeepr-recordings:/recordings \
  --mount type=bind,source="$PWD/secrets/twitch_client_id.txt",target=/run/secrets/Twitch__ClientId,readonly \
  --mount type=bind,source="$PWD/secrets/twitch_client_secret.txt",target=/run/secrets/Twitch__ClientSecret,readonly \
  streamkeepr
```

The two files must have the ownership and mode described above. Streamlink is pinned at 8.6.3 and can be bumped with `--build-arg STREAMLINK_VERSION=<version>`.

