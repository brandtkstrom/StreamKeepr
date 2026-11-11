#!/usr/bin/env bash
# Builds and exercises the Compose deployment against the hosting contract.
#
# Production phase: contract, health, non-root UID, volume, secrets, logging,
# clean stop, and restart. Mock phase: a real growing recording through the
# committed fake Streamlink, stop-mid-recording finalization, cookie continuity,
# and a seeded deep link.
#
# Everything is isolated: a temporary secrets directory, a unique project,
# container and image name, and a disposable volume. It never touches an
# operator's stack, recordings, or secrets.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
mock_override="$repo_root/scripts/compose-smoke.mock.yaml"

project="streamkeepr-smoke-$$"
suffix="$$"
port="${COMPOSE_SMOKE_PORT:-18080}"
container="streamkeepr-smoke-${suffix}"
image="streamkeepr-smoke:${suffix}"
temp_dir="$(mktemp -d "${TMPDIR:-/tmp}/streamkeepr-compose-smoke.XXXXXX")"
base_compose=(docker compose --project-name "$project" --file "$repo_root/compose.yaml")
mock_compose=(docker compose --project-name "$project" --file "$repo_root/compose.yaml" --file "$mock_override")
compose=("${base_compose[@]}")

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

    # Both compose commands carry the unique smoke project name, so this cannot
    # touch an operator's real stack.
    "${base_compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    "${mock_compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
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
export COMPOSE_SMOKE_FIXTURES="$repo_root/tests/fixtures"
base_compose=(docker compose --project-name "$project" --file "$repo_root/compose.yaml")
mock_compose=(docker compose --project-name "$project" --file "$repo_root/compose.yaml" --file "$mock_override")
compose=("${base_compose[@]}")

wait_for_url() {
    local url="$1"
    local label="$2"
    local attempts="${3:-90}"

    for _ in $(seq 1 "$attempts"); do
        if curl --fail --silent --show-error --max-time 5 "$url" >/dev/null 2>&1; then
            return 0
        fi
        sleep 2
    done
    fail "$label ($url) did not become available"
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

# Compose file secrets are bind mounts on Linux. The container runs as UID 1654,
# so make the disposable dummy files readable by that UID without making them
# world-readable. A one-shot root container performs the chown on host files.
echo "Making disposable secrets readable by UID 1654"
docker run --rm --user 0:0 --volume "$STREAMKEEP_SECRETS_DIR:/secrets" \
    --entrypoint sh "$image" -c 'chown 1654:1654 /secrets/*.txt && chmod 400 /secrets/*.txt'

# Read the exact file-to-target mapping a Compose secret mount produces, as the
# same UID the app uses. This is the check that a 0600 file owned by the runner
# would fail on Linux; Docker Desktop on macOS maps ownership, so the Linux CI
# job is the real bind-mount permission check.
docker run --rm --user 1654:1654 \
    --mount "type=bind,source=$STREAMKEEP_SECRETS_DIR/twitch_client_id.txt,target=/run/secrets/Twitch__ClientId,readonly" \
    --mount "type=bind,source=$STREAMKEEP_SECRETS_DIR/twitch_client_secret.txt,target=/run/secrets/Twitch__ClientSecret,readonly" \
    --entrypoint sh "$image" \
    -c 'test "$(cat /run/secrets/Twitch__ClientId)" = "dummy-client-id" && test "$(cat /run/secrets/Twitch__ClientSecret)" = "dummy-client-secret"'
echo "UID 1654 can read the mounted secrets"

echo "Starting the production Compose service"
"${compose[@]}" up --detach --no-build

wait_for_url "http://127.0.0.1:$port/health/live" "/health/live"
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

echo "Stopping the production service"
started_at="$(date +%s)"
"${compose[@]}" stop
stopped_after=$(( $(date +%s) - started_at ))
exit_code="$(docker inspect --format '{{.State.ExitCode}}' "$container")"
[ "$exit_code" = "0" ] || fail "container did not stop cleanly (exit $exit_code)"
[ "$stopped_after" -le 55 ] || fail "container took ${stopped_after}s to stop"
echo "Stopped cleanly in ${stopped_after}s (exit 0)"

echo "Restarting the production service"
"${compose[@]}" start
wait_for_url "http://127.0.0.1:$port/health/live" "/health/live after restart"
assert_status "http://127.0.0.1:$port/" 200
keys_after="$(docker run --rm --user 1654:1654 --volume "$volume:/recordings" \
    --entrypoint sh "$image" -c 'cd /recordings/.streamkeepr/keys && ls -1 | sort')"
[ "$keys_before" = "$keys_after" ] || fail "Data Protection keys changed across restart"
echo "Production phase passed; keys survived the restart"

"${compose[@]}" down --volumes --remove-orphans

echo "Starting the test-only Mock Compose service"
compose=("${mock_compose[@]}")
"${compose[@]}" up --detach --no-build
wait_for_url "http://127.0.0.1:$port/health/ready" "Mock /health/ready" 120

cookie_jar="$temp_dir/mock-sk-auth.cookies"
headers_before="$temp_dir/mock-headers-before.txt"
curl --fail --silent --show-error --cookie-jar "$cookie_jar" \
    --dump-header "$headers_before" "http://127.0.0.1:$port/" --output /dev/null
cookie_before="$(awk '$6 == "sk-auth" { print $7 }' "$cookie_jar" | tail -1)"
[ -n "$cookie_before" ] || fail "the Mock profile did not issue an sk-auth cookie"

mock_root="/tmp/streamkeepr-mock/recordings"
active_part=""
for _ in $(seq 1 120); do
    active_part="$(docker exec "$container" sh -c '
        root="$1"
        latest=$(ls -1 "$root"/mock_rain/*/p[0-9][0-9][0-9].m3u8 2>/dev/null | sort | tail -1)
        [ -n "$latest" ] || exit 1
        grep -q "^#EXT-X-ENDLIST$" "$latest" && exit 1
        printf "%s\n" "$latest"
    ' sh "$mock_root" 2>/dev/null)" || active_part=""
    if [ -n "$active_part" ]; then
        break
    fi
    sleep 2
done
[ -n "$active_part" ] || fail "no active Mock part playlist without ENDLIST appeared"
growing_session="$(basename "$(dirname "$active_part")")"
echo "Active Mock part: $active_part (session $growing_session)"

echo "Stopping the Mock service mid-recording"
started_at="$(date +%s)"
"${compose[@]}" stop
stopped_after=$(( $(date +%s) - started_at ))
exit_code="$(docker inspect --format '{{.State.ExitCode}}' "$container")"
[ "$exit_code" = "0" ] || fail "Mock container did not stop cleanly (exit $exit_code)"
[ "$stopped_after" -le 55 ] || fail "Mock container took ${stopped_after}s to stop"
echo "Mock service stopped cleanly in ${stopped_after}s (exit 0)"

mkdir -p "$temp_dir/mock-recordings"
docker cp "$container:$mock_root/." "$temp_dir/mock-recordings/"
python3 - "$temp_dir/mock-recordings" <<'PY'
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
parts = sorted(root.glob("*/[0-9]*/p[0-9][0-9][0-9].m3u8"))
if not parts:
    print("No part playlists were copied from the Mock recordings root.", file=sys.stderr)
    sys.exit(1)

failures = []
for part in parts:
    lines = [line.strip() for line in part.read_text().splitlines() if line.strip()]
    endlist_count = lines.count("#EXT-X-ENDLIST")
    if endlist_count != 1 or lines[-1] != "#EXT-X-ENDLIST":
        failures.append(
            f"{part.relative_to(root)}: {endlist_count} ENDLIST marker(s), "
            f"last line {lines[-1] if lines else '<empty>'!r}"
        )

if failures:
    print("Part playlists did not finalize:", file=sys.stderr)
    for failure in failures:
        print(f"  - {failure}", file=sys.stderr)
    sys.exit(1)

print(f"{len(parts)} part playlists end with exactly one ENDLIST")
PY

echo "Restarting the same Mock container"
"${compose[@]}" start
wait_for_url "http://127.0.0.1:$port/health/ready" "Mock /health/ready after restart" 120

headers_after="$temp_dir/mock-headers-after.txt"
assert_status "http://127.0.0.1:$port/" 200
curl --fail --silent --show-error --cookie "$cookie_jar" \
    --dump-header "$headers_after" "http://127.0.0.1:$port/" --output /dev/null
if grep -Eqi '^set-cookie:[[:space:]]*sk-auth=' "$headers_after"; then
    fail "the Mock profile issued a new sk-auth cookie after restart"
fi
cookie_after="$(awk '$6 == "sk-auth" { print $7 }' "$cookie_jar" | tail -1)"
[ "$cookie_before" = "$cookie_after" ] || fail "the sk-auth cookie value changed across restart"
echo "sk-auth cookie survived the restart unchanged"

deep_link="http://127.0.0.1:$port/r/20261108T180000Z_9100001"
assert_status "$deep_link" 200
page="$(curl --fail --silent --show-error "$deep_link")"
[[ "$page" == *'data-selected-recording="20261108T180000Z_9100001"'* ]] \
    || fail "the seeded Mock deep link did not render"
assert_status "http://127.0.0.1:$port/media/mock_nova/20261108T180000Z_9100001/index.m3u8" 200
echo "Seeded deep link and media survived the restart"

echo "Compose smoke test passed"
