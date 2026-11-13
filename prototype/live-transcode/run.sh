#!/usr/bin/env bash
# Disposable prototype launcher for issue #147. Not production code. Do not merge.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MODE="${1:-none}"
IMAGE="${IMAGE:-streamkeepr-live-transcode-prototype:local}"
RESULTS_DIR="${RESULTS_DIR:-$HERE/results}"

case "$MODE" in
    none|vaapi|nvidia|all) ;;
    *)
        echo "usage: $0 none|vaapi|nvidia|all" >&2
        exit 2
        ;;
esac

mkdir -p "$RESULTS_DIR"
docker build -t "$IMAGE" "$HERE"

ARGS=(--rm -v "$RESULTS_DIR:/out" -e "REQUESTED_GPU=$MODE")

case "$MODE" in
    vaapi|all)
        if [ ! -e /dev/dri/renderD128 ]; then
            echo "error: /dev/dri/renderD128 is not available" >&2
            exit 2
        fi
        GID="$(stat -c '%g' /dev/dri/renderD128)"
        ARGS+=(--device=/dev/dri:/dev/dri --group-add "$GID")
        ;;
esac

case "$MODE" in
    nvidia|all)
        ARGS+=(--gpus all)
        ;;
esac

docker run "${ARGS[@]}" "$IMAGE"
echo "Results: $RESULTS_DIR/summary.md"
