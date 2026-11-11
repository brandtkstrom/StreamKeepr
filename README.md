# StreamKeepr

## Running the new web app

The new app in `src/StreamKeepr` validates its configuration at startup, so it needs Twitch app credentials even while it is only a shell. For local development:

```bash
dotnet user-secrets --project src/StreamKeepr set "Twitch:ClientId" "<client-id>"
dotnet user-secrets --project src/StreamKeepr set "Twitch:ClientSecret" "<client-secret>"
dotnet user-secrets --project src/StreamKeepr set "Storage:RecordingsRoot" "$PWD/recordings"
dotnet run --project src/StreamKeepr
```

Then open <http://localhost:5080>. Tests run with `dotnet test tests/StreamKeepr.Tests`.

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
