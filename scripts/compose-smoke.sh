#!/usr/bin/env bash
# Builds and exercises the Compose deployment against the hosting contract.
#
# It uses a temporary secrets directory, a unique project/container name, a
# separate image tag, and an isolated recordings volume, so it never reads or
# replaces an operator's real secrets, recordings, or running stack.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"

project="streamkeepr-smoke-$$"
suffix="$$"
port="${COMPOSE_SMOKE_PORT:-18080}"
container="streamkeepr-smoke-${suffix}"
image="streamkeepr-smoke:${suffix}"
temp_dir="$(mktemp -d "${TMPDIR:-/tmp}/streamkeepr-compose-smoke.XXXXXX")"
compose=(docker compose --project-name "$project" --file "$repo_root/compose.yaml")

fail() {
    echo "::error::$*" >&2
    "${compose[@]}" ps >&2 || true
    "${compose[@]}" logs --no-color --tail 200 >&2 || true
    exit 1
}

cleanup() {
    if [ "${COMPOSE_SMOKE_KEEP:-0}" = "1" ]; then
        echo "Keeping project $project, container $container, image $image, and $temp_dir"
        return
    fi

    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    docker image rm --force "$image" >/dev/null 2>&1 || true
    rm -rf "$temp_dir"
}
trap cleanup EXIT

if ! docker info >/dev/null 2>&1; then
    echo "Docker is unavailable; the Compose smoke test needs a running daemon." >&2
    exit 1
fi

# These are the only values the smoke run overrides. The base file keeps the
# production defaults; validate those before applying the isolated values.
unset STREAMKEEP_IMAGE STREAMKEEP_CONTAINER_NAME STREAMKEEP_PORT
export STREAMKEEP_SECRETS_DIR="$temp_dir/secrets"

mkdir -p "$STREAMKEEP_SECRETS_DIR"
printf '%s\n' 'dummy-client-id' > "$STREAMKEEP_SECRETS_DIR/twitch_client_id.txt"
printf '%s\n' 'dummy-client-secret' > "$STREAMKEEP_SECRETS_DIR/twitch_client_secret.txt"
chmod 600 "$STREAMKEEP_SECRETS_DIR"/*.txt

echo "Validating docker compose config against the hosting contract"
config_json="$("${compose[@]}" config --format json)"
printf '%s' "$config_json" | python3 -c '
import json
import sys

config = json.load(sys.stdin)
service = config["services"]["stream-keepr"]
errors = []


def check(condition, message):
    if not condition:
        errors.append(message)


check(service.get("image") == "stream-keepr:latest", "image must default to stream-keepr:latest")
check(service.get("container_name") == "stream-keepr", "container_name must be stream-keepr")
check(service.get("restart") == "unless-stopped", "restart must be unless-stopped")
check(service.get("init") is True, "init must be true")
check(str(service.get("stop_grace_period")) == "50s", "stop_grace_period must be 50s")

ports = service.get("ports", [])
check(
    any(
        str(entry.get("target")) == "8080"
        and str(entry.get("published")) == "8080"
        and entry.get("protocol") == "tcp"
        for entry in ports
    ),
    "ports must publish plain HTTP 8080:8080",
)
check("healthcheck" not in service, "Compose must inherit the image healthcheck")

environment = service.get("environment", {})
check(environment.get("ASPNETCORE_ENVIRONMENT") == "Production", "ASPNETCORE_ENVIRONMENT must be Production")
check(environment.get("Storage__RecordingsRoot") == "/recordings", "Storage__RecordingsRoot must be /recordings")
check("TZ" in environment, "TZ must be set")

service_secrets = {entry.get("source") for entry in service.get("secrets", [])}
check("Twitch__ClientId" in service_secrets, "Twitch__ClientId secret must be mounted")
check("Twitch__ClientSecret" in service_secrets, "Twitch__ClientSecret secret must be mounted")

volumes = service.get("volumes", [])
check(
    any(
        entry.get("type") == "volume"
        and entry.get("source") == "recordings"
        and entry.get("target") == "/recordings"
        for entry in volumes
    ),
    "recordings volume must mount at /recordings",
)

logging = service.get("logging", {})
check(logging.get("driver") == "json-file", "logging must use json-file")
log_options = logging.get("options", {})
check(str(log_options.get("max-size")) == "10m", "log max-size must be 10m")
check(str(log_options.get("max-file")) == "5", "log max-file must be 5")

top_level_secrets = config.get("secrets", {})
check("Twitch__ClientId" in top_level_secrets, "Twitch__ClientId must have a file source")
check("Twitch__ClientSecret" in top_level_secrets, "Twitch__ClientSecret must have a file source")

if errors:
    print("Compose contract check failed:", file=sys.stderr)
    for error in errors:
        print(f"  - {error}", file=sys.stderr)
    sys.exit(1)

print("Compose contract check passed")
'

export STREAMKEEP_IMAGE="$image"
export STREAMKEEP_CONTAINER_NAME="$container"
export STREAMKEEP_PORT="$port"
compose=(docker compose --project-name "$project" --file "$repo_root/compose.yaml")

wait_for_health() {
    local url="http://127.0.0.1:$port/health/live"
    for _ in $(seq 1 90); do
        if curl --fail --silent --show-error --max-time 5 "$url" >/dev/null 2>&1; then
            return 0
        fi
        sleep 2
    done
    fail "$url did not become healthy"
}

wait_for_media() {
    local url="http://127.0.0.1:$port/media/seedstreamer/20261111T000000Z_12345/index.m3u8"
    for _ in $(seq 1 60); do
        if curl --fail --silent --show-error --max-time 5 "$url" >/dev/null 2>&1; then
            return 0
        fi
        sleep 2
    done
    fail "$url did not become available after recovery"
}

assert_status() {
    local url="$1"
    local expected="$2"
    local actual
    actual="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' --max-time 10 "$url")"
    if [ "$actual" != "$expected" ]; then
        fail "$url returned HTTP $actual, expected $expected"
    fi
}

echo "Building $image"
"${compose[@]}" build

echo "Starting the Compose service"
"${compose[@]}" up --detach --no-build

wait_for_health
assert_status "http://127.0.0.1:$port/" 200
echo "Home page and /health/live are healthy"

uid="$(docker exec "$container" id -u)"
[ "$uid" = "1654" ] || fail "container runs as UID $uid, expected 1654"
recordings_uid="$(docker exec "$container" stat -c '%u' /recordings)"
[ "$recordings_uid" = "1654" ] || fail "/recordings is owned by UID $recordings_uid, expected 1654"
echo "Container UID: $uid; /recordings UID: $recordings_uid"

# Startup options require Twitch:ClientId and Twitch:ClientSecret, and neither
# is present as an environment variable or in appsettings.json. A healthy host
# therefore proves AddKeyPerFile loaded /run/secrets.
docker exec "$container" test -s /run/secrets/Twitch__ClientId \
    || fail "Twitch__ClientId is not mounted at /run/secrets"
docker exec "$container" test -s /run/secrets/Twitch__ClientSecret \
    || fail "Twitch__ClientSecret is not mounted at /run/secrets"
[ "$(docker exec "$container" cat /run/secrets/Twitch__ClientId)" = "dummy-client-id" ] \
    || fail "Twitch__ClientId secret content does not match"
docker exec "$container" sh -c 'test -z "${Twitch__ClientId:-}"' \
    || fail "Twitch credentials must come from secrets, not the environment"
echo "Secrets are mounted and were used to pass startup options validation"

healthcheck="$(docker inspect --format '{{json .Config.Healthcheck}}' "$container")"
[[ "$healthcheck" == *"/health/live"* ]] || fail "image healthcheck does not probe /health/live"
log_config="$(docker inspect --format '{{json .HostConfig.LogConfig}}' "$container")"
python3 -c '
import json
import sys

logging = json.loads(sys.argv[1])
options = logging.get("Config") or {}
assert logging.get("Type") == "json-file", logging
assert options.get("max-size") == "10m", logging
assert options.get("max-file") == "5", logging
' "$log_config" || fail "container json-file logging rotation is not configured"
echo "Image healthcheck and json-file log rotation are inherited"

volume="$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/recordings"}}{{.Name}}{{end}}{{end}}' "$container")"
[ -n "$volume" ] || fail "could not resolve the recordings volume"

keys_before="$(docker run --rm --user 1654:1654 --volume "$volume:/recordings" \
    --entrypoint sh "$image" -c 'cd /recordings/.streamkeepr/keys && ls -1 | sort')"
[ -n "$keys_before" ] || fail "Data Protection keys were not created on the recordings volume"
docker run --rm --user 1654:1654 --volume "$volume:/recordings" \
    --entrypoint sh "$image" -c 'test "$(stat -c %a /recordings/.streamkeepr/keys)" = 700' \
    || fail "Data Protection key directory is not mode 0700"
echo "Data Protection keys exist on the recordings volume"

echo "Stopping the service"
started_at="$(date +%s)"
"${compose[@]}" stop
stopped_after=$(( $(date +%s) - started_at ))
exit_code="$(docker inspect --format '{{.State.ExitCode}}' "$container")"
[ "$exit_code" = "0" ] || fail "container did not stop cleanly (exit $exit_code)"
[ "$stopped_after" -le 55 ] || fail "container took ${stopped_after}s to stop"
echo "Stopped cleanly in ${stopped_after}s (exit 0)"

# The Mock profile does not exist in this tree, so a real Twitch recording
# cannot be started in Compose. Seed the on-disk state a graceful stop leaves
# behind (a Recording manifest with one part and no index) and let startup
# recovery finalize it. This covers the ENDLIST/recovery path honestly without
# weakening the production image.
seed_root="$temp_dir/seed/seedstreamer/20261111T000000Z_12345"
mkdir -p "$seed_root"
cat > "$seed_root/session.json" <<'JSON'
{
  "version": 1,
  "id": "20261111T000000Z_12345",
  "channel": "seedstreamer",
  "streamId": "12345",
  "startedAt": "2026-11-11T00:00:00+00:00",
  "status": "Recording",
  "metadata": [
    {
      "at": "2026-11-11T00:00:00+00:00",
      "title": "Seeded interrupted recording",
      "game": "Testing"
    }
  ]
}
JSON
cat > "$seed_root/p001.m3u8" <<'PLAYLIST'
#EXTM3U
#EXT-X-VERSION:7
#EXT-X-TARGETDURATION:6
#EXT-X-MEDIA-SEQUENCE:0
#EXT-X-PLAYLIST-TYPE:EVENT
#EXT-X-INDEPENDENT-SEGMENTS
#EXT-X-MAP:URI="init_001.mp4"
#EXT-X-PROGRAM-DATE-TIME:2026-11-11T00:00:00Z
#EXTINF:6,
p001_00000.m4s
#EXT-X-ENDLIST
PLAYLIST
printf 'seed' > "$seed_root/p001_00000.m4s"
printf 'seed' > "$seed_root/init_001.mp4"

docker run --rm --user 1654:1654 --volume "$volume:/recordings" \
    --volume "$temp_dir/seed:/seed:ro" --entrypoint sh "$image" \
    -c 'cp -R /seed/. /recordings/ && chown -R 1654:1654 /recordings/seedstreamer'

echo "Restarting the service"
"${compose[@]}" start
wait_for_health
wait_for_media
assert_status "http://127.0.0.1:$port/r/20261111T000000Z_12345" 200
curl --fail --silent --show-error "http://127.0.0.1:$port/r/20261111T000000Z_12345" \
    | grep -q 'data-selected-recording="20261111T000000Z_12345"' \
    || fail "deep link did not render the recording page"

playlist="$(curl --fail --silent --show-error "http://127.0.0.1:$port/media/seedstreamer/20261111T000000Z_12345/index.m3u8")"
endlist_count="$(printf '%s\n' "$playlist" | grep -c '^#EXT-X-ENDLIST$' || true)"
[ "$endlist_count" = "1" ] || fail "recovered index has $endlist_count ENDLIST markers, expected 1"
docker run --rm --user 1654:1654 --volume "$volume:/recordings" \
    --entrypoint sh "$image" -c "grep -q '\"status\": \"Complete\"' /recordings/seedstreamer/20261111T000000Z_12345/session.json" \
    || fail "recovery did not mark the seeded session Complete"
echo "Recovery finalized the interrupted session with one ENDLIST"

keys_after="$(docker run --rm --user 1654:1654 --volume "$volume:/recordings" \
    --entrypoint sh "$image" -c 'cd /recordings/.streamkeepr/keys && ls -1 | sort')"
[ "$keys_before" = "$keys_after" ] || fail "Data Protection keys changed across restart"
echo "Data Protection keys survived the restart"

echo "Restarting once more with docker compose restart"
"${compose[@]}" restart
wait_for_health
assert_status "http://127.0.0.1:$port/" 200
wait_for_media
assert_status "http://127.0.0.1:$port/r/20261111T000000Z_12345" 200
curl --fail --silent --show-error "http://127.0.0.1:$port/r/20261111T000000Z_12345" \
    | grep -q 'data-selected-recording="20261111T000000Z_12345"' \
    || fail "deep link did not render after the restart"
keys_final="$(docker run --rm --user 1654:1654 --volume "$volume:/recordings" \
    --entrypoint sh "$image" -c 'cd /recordings/.streamkeepr/keys && ls -1 | sort')"
[ "$keys_before" = "$keys_final" ] || fail "Data Protection keys changed across restart"
echo "Restart preserved the recording, deep link, and Data Protection keys"

echo "Compose smoke test passed"
