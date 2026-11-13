# Deploying Stream Keepr with Docker Compose

> **Private home use only.** These instructions are for a trusted private home or LAN deployment. StreamKeepr is a personal project, not a hosted service. Public reverse-proxy exposure, multi-tenant use, and third-party distribution are unsupported; use your own hardening if you expose it. The image is published for the owner's convenience, not as a supported distribution channel. See [`../SECURITY.md`](../SECURITY.md).

This folder holds what you need to run the image from `ghcr.io/brandtkstrom/streamkeepr` on your own private host. You don't need the source code or a build.

| File | Purpose |
| --- | --- |
| `compose.yaml` | The service, with every setting explained inline |
| `.env.example` | Optional overrides: image tag, host port, time zone |
| `secrets/*.txt.example` | Empty templates for the Twitch credentials |

The image runs on `linux/amd64` and `linux/arm64`. It includes Streamlink and FFmpeg, runs as a non-root user (UID 1654), and has a built-in health check.

## 1. Register a Twitch application

1. Open the [Twitch developer console](https://dev.twitch.tv/console/apps) and choose **Register Your Application**.
2. Add an **OAuth Redirect URL** that matches the address you'll sign in from exactly, followed by `/signin-twitch`:
   - `https://streamkeepr.home.example/signin-twitch` behind a private HTTPS reverse proxy on your home network;
   - `http://localhost:8080/signin-twitch` if you only sign in on the host itself.
3. Set the category to **Application Integration** and create the app.
4. Copy the **Client ID**, then choose **New Secret** and copy the **Client Secret**.

## 2. Fill in the secrets

Copy this folder to the host. Then, inside it, create the real secret files from the empty templates:

```sh
cp secrets/twitch_client_id.txt.example     secrets/twitch_client_id.txt
cp secrets/twitch_client_secret.txt.example secrets/twitch_client_secret.txt
```

Put each value on a single line in its file: the client ID in `twitch_client_id.txt` and the client secret in `twitch_client_secret.txt`. Surrounding whitespace is trimmed.

On Linux, Compose bind-mounts these files, so the container user must be able to read them:

```sh
sudo chown 1654:1654 secrets/*.txt
sudo chmod 400 secrets/*.txt
```

On Docker Desktop for macOS or Windows, file ownership is mapped for you, and the `chown` is harmless.

`secrets/.gitignore` ignores everything except the templates, so the filled-in files can't be committed by accident. Never put the values in `compose.yaml` or `.env`.

### Optional: subscriber-only streams

Streamlink needs your own twitch.tv login token to record streams that are only for subscribers. This is the browser's `auth-token` cookie on twitch.tv, not the app secret.

1. Copy `secrets/recorder_twitch_user_oauth_token.txt.example` to `secrets/recorder_twitch_user_oauth_token.txt`.
2. Paste the token into it.
3. Apply the same `chown` and `chmod`.
4. Uncomment both `Recorder__TwitchUserOAuthToken` entries in `compose.yaml`.

Rotating the token is manual. If subscriber recordings start failing, get a fresh token, replace the file, and restart the service.

## 3. Choose settings (optional)

```sh
cp .env.example .env
```

`.env` sets the image tag (`STREAMKEEP_VERSION`, default `1.0.0`), the host port (`STREAMKEEP_PORT`, default `8080`) and the time zone for logs (`TZ`).

The rest of the settings are commented out under `environment:` in `compose.yaml`:

| Setting | When to set it |
| --- | --- |
| `Auth__AllowedTwitchLogins__0` | Whenever other people can reach the app. Without it, any Twitch account that signs in is an admin, and startup logs a warning. Add `__1`, `__2` and so on for more logins. |
| `Twitch__Channels__0` | To watch a first channel on a fresh deployment. After that, add and remove channels in **Settings**. |
| `Storage__MinFreeGb` | To change when recording stops for low disk space (default 10 GB). |
| `Recorder__Quality` | To record something other than Streamlink's `best`. |
| `Twitch__EventSubTransport` | To try `Conduit` push: it uses the app credentials, keeps working without an admin signed in, and covers far more than five channels. The default `WebSocket` needs a signed-in admin. Setting it back to `WebSocket` deletes the conduit on the next start. |
| `Hosting__ForwardedHeaders__…` | When the app runs behind an HTTPS reverse proxy. See the next section. |

Uncomment a setting to use it; don't leave one set to an empty value, because empty values fail validation at startup.

## 4. Start it

```sh
docker compose up -d
docker compose logs -f stream-keepr
```

Then open `http://<host>:8080`. Viewing and playback work from machines on your trusted home network that can reach the port. When the service has started, `docker compose ps` shows it as `healthy`.

## HTTPS reverse proxy

Twitch sign-in needs HTTPS, except from `localhost`, so use a TLS-terminating reverse proxy (Caddy, Traefik, nginx and so on) for private remote administration on your home network:

1. Point the proxy at `http://<host>:8080`, and use the proxy's `https://` address (the one you registered with Twitch) as the redirect URL.
2. Tell the app to trust the proxy's `X-Forwarded-*` headers by uncommenting one of these and setting it to the proxy's address or network:

   ```yaml
   Hosting__ForwardedHeaders__KnownProxies__0: 172.18.0.1
   Hosting__ForwardedHeaders__KnownNetworks__0: 172.18.0.0/16
   ```

Only trust the proxy. If you trust a wider network, any client on it can claim to be HTTPS. If sign-in returns `authError=httpsRequired`, the app isn't trusting the proxy yet.

## Updating

To update, set a new tag (`STREAMKEEP_VERSION` in `.env`, or the `image:` line), then run:

```sh
docker compose pull
docker compose up -d
```

Compose stops the old container gracefully. Active recordings finalize, and the new container resumes any session that is still live.

## Stopping and data

- `docker compose stop` stops the service, and active recordings finalize within the 50 s grace period.
- `docker compose down` removes the container but keeps the `recordings` volume, which holds the recordings, channel list and sign-in keys.
- **`docker compose down --volumes` deletes every recording.** Only use it to start over.

To back up, copy the volume's contents. `docker volume inspect stream-keepr_recordings` shows where the volume lives on the host. To keep recordings in an ordinary folder instead, use the bind mount described in `compose.yaml`.

## Health

| Endpoint | Meaning |
| --- | --- |
| `/health/live` | The process is up. The image's health check uses this. |
| `/health/ready` | Recording can work: there's enough disk space, and Twitch polling or push notifications are current. |
