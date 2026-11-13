#!/usr/bin/env bash
# Disposable prototype runner for issue #147. Not production code. Do not merge.
set -u

RESULTS_DIR="${RESULTS_DIR:-/out}"
REQUESTED_GPU="${REQUESTED_GPU:-none}"
mkdir -p "$RESULTS_DIR"
exec > >(tee "$RESULTS_DIR/run.log") 2>&1

WORK="$(mktemp -d /tmp/live-transcode.XXXXXX)"
FIXTURE="$WORK/fixture"
mkdir -p "$FIXTURE"
HTTP_PID=""

cleanup() {
    if [ -n "$HTTP_PID" ]; then
        kill "$HTTP_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT

say() { printf '%s\n' "$*"; }

{
    date -u
    uname -a
    dpkg --print-architecture
    ffmpeg -hide_banner -version | head -n 4
    if [ -d /dev/dri ]; then ls -l /dev/dri; else echo "no /dev/dri"; fi
    if command -v vainfo >/dev/null 2>&1; then vainfo 2>&1 || true; else echo "vainfo unavailable"; fi
    if command -v nvidia-smi >/dev/null 2>&1; then nvidia-smi 2>&1 || true; else echo "nvidia-smi unavailable"; fi
    echo "REQUESTED_GPU=$REQUESTED_GPU"
} > "$RESULTS_DIR/environment.txt" 2>&1

gen_part() {
    local n="$1"
    local padded
    padded="$(printf '%03d' "$n")"
    ffmpeg -hide_banner -loglevel error -y \
        -f lavfi -i "testsrc2=size=320x180:rate=30:duration=6" \
        -f lavfi -i "sine=frequency=$((300 + n * 100)):sample_rate=48000:duration=6" \
        -map 0:v -map 1:a \
        -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
        -g 60 -keyint_min 60 -sc_threshold 0 \
        -c:a aac -b:a 96k \
        -f hls -hls_segment_type fmp4 -hls_time 2 -hls_list_size 0 \
        -hls_playlist_type event \
        -hls_flags +temp_file+independent_segments+program_date_time \
        -hls_fmp4_init_filename "init_${padded}.mp4" \
        -hls_segment_filename "p${padded}_%05d.m4s" \
        "p${padded}.m3u8"
}

probe_encoder() {
    local encoder="$1"
    local log="$RESULTS_DIR/probe-$encoder.log"
    case "$encoder" in
        libx264)
            ffmpeg -hide_banner -loglevel error -y \
                -f lavfi -i "testsrc2=size=320x180:rate=30:duration=0.2" \
                -c:v libx264 -f null - >"$log" 2>&1
            ;;
        vaapi)
            if [ ! -e /dev/dri/renderD128 ]; then
                echo "no /dev/dri/renderD128" >"$log"
                return 2
            fi
            ffmpeg -hide_banner -loglevel error -y \
                -vaapi_device /dev/dri/renderD128 \
                -f lavfi -i "testsrc2=size=320x180:rate=30:duration=0.2" \
                -vf 'format=nv12,hwupload' -c:v h264_vaapi -f null - >"$log" 2>&1
            ;;
        nvenc)
            if [ ! -e /dev/nvidia0 ] && ! command -v nvidia-smi >/dev/null 2>&1; then
                echo "no NVIDIA device or toolkit" >"$log"
                return 2
            fi
            ffmpeg -hide_banner -loglevel error -y \
                -f lavfi -i "testsrc2=size=320x180:rate=30:duration=0.2" \
                -c:v h264_nvenc -f null - >"$log" 2>&1
            ;;
        *)
            echo "unknown encoder $encoder" >"$log"
            return 1
            ;;
    esac
}

run_gate() {
    local encoder="$1"
    local out="$RESULTS_DIR/gate-$encoder"
    rm -rf "$out"
    mkdir -p "$out"

    stitch.py init "$FIXTURE/live.m3u8" "$FIXTURE/p001.m3u8"

    cp "$FIXTURE/live.m3u8" "$out/live-before.m3u8"
    local args=(
        -hide_banner -loglevel info -nostats
        -fflags +genpts+discardcorrupt
        -live_start_index 0
        -i "http://127.0.0.1:8099/live.m3u8"
        -map 0:v:0 -map 0:a:0?
    )
    case "$encoder" in
        libx264)
            args+=(-c:v libx264 -preset veryfast -crf 23 -g 60 -keyint_min 60 -sc_threshold 0)
            ;;
        vaapi)
            args+=(
                -vaapi_device /dev/dri/renderD128
                -vf 'format=nv12,hwupload'
                -c:v h264_vaapi -b:v 1M -maxrate 1M -bufsize 2M -g 60
            )
            ;;
        nvenc)
            args+=(-c:v h264_nvenc -b:v 1M -maxrate 1M -bufsize 2M -g 60)
            ;;
    esac
    args+=(
        -c:a aac -b:a 96k
        -f hls -hls_segment_type fmp4 -hls_time 2 -hls_list_size 0
        -hls_playlist_type event
        -hls_flags +temp_file+independent_segments+program_date_time
        -hls_fmp4_init_filename init.mp4
        -hls_segment_filename "$out/proxy_%05d.m4s"
        "$out/index.m3u8"
    )

    ffmpeg "${args[@]}" >"$out/ffmpeg.log" 2>&1 &
    local pid=$!

    local started=0
    for _ in $(seq 1 300); do
        if compgen -G "$out/proxy_*.m4s" >/dev/null && [ -s "$out/index.m3u8" ]; then
            started=1
            break
        fi
        sleep 0.2
    done
    if [ "$started" -eq 0 ]; then
        echo "fail: no output media" >"$out/result.txt"
        kill "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
        return 1
    fi

    # Give the encoder a moment at the first live edge, then append part 2.
    sleep 3
    stitch.py append "$FIXTURE/live.m3u8" "$FIXTURE/p002.m3u8"
    cp "$FIXTURE/live.m3u8" "$out/live-after.m3u8"

    # Poll for the proxy to consume part 2. A local-file/HTTP playlist that is
    # not re-read will time out here, which is a real prototype finding.
    local grew=0
    for _ in $(seq 1 300); do
        if grep -q 'p002_00002.m4s' "$out/ffmpeg.log" 2>/dev/null; then
            grew=1
            break
        fi
        sleep 0.2
    done
    if [ "$grew" -eq 0 ]; then
        echo "fail: part 2 was not consumed within 60 s" >"$out/result.txt"
        kill -INT "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
        return 1
    fi

    # Let the encoder flush the accepted part-2 frames before graceful stop.
    sleep 2
    kill -INT "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true

    local endlist=0
    grep -q '#EXT-X-ENDLIST' "$out/index.m3u8" 2>/dev/null && endlist=1
    local duration=""
    duration="$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$out/index.m3u8" 2>/dev/null | head -n1 || true)"
    local vcodec=""
    vcodec="$(ffprobe -v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 "$out/index.m3u8" 2>/dev/null | head -n1 || true)"
    local acodec=""
    acodec="$(ffprobe -v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0 "$out/index.m3u8" 2>/dev/null | head -n1 || true)"
    local segments=0
    segments="$(grep -c '^[^#]' "$out/index.m3u8" 2>/dev/null || true)"

    local pass=0
    if [ "$endlist" -eq 1 ] \
        && [ "$vcodec" = "h264" ] \
        && [ "$acodec" = "aac" ] \
        && awk -v d="${duration:-0}" 'BEGIN { exit !(d + 0 >= 10) }' \
        && [ "$segments" -ge 2 ]; then
        pass=1
    fi

    {
        echo "encoder=$encoder"
        echo "pass=$pass"
        echo "endlist=$endlist"
        echo "duration=$duration"
        echo "video_codec=$vcodec"
        echo "audio_codec=$acodec"
        echo "segments=$segments"
    } >"$out/result.txt"

    [ "$pass" -eq 1 ]
}

probe_label() {
    local status="$1"
    case "$status" in
        0) echo "PASS" ;;
        2) echo "UNVERIFIED" ;;
        *) echo "FAILED" ;;
    esac
}

mkdir -p "$RESULTS_DIR"
cd "$FIXTURE" || exit 1
gen_part 1 || { echo "fixture part 1 generation failed" >&2; exit 1; }
# Simulate a live part boundary: part 2 starts after part 1's media time.
sleep 6
gen_part 2 || { echo "fixture part 2 generation failed" >&2; exit 1; }
(cd "$FIXTURE" && sha256sum p001.m3u8 p002.m3u8 init_*.mp4 p*.m4s | sort) \
    > "$WORK/source-before.sha256"

stitch.py init "$FIXTURE/live.m3u8" "$FIXTURE/p001.m3u8" \
    || { echo "initial stitch failed" >&2; exit 1; }
python3 -m http.server 8099 --bind 127.0.0.1 --directory "$FIXTURE" \
    >"$WORK/http.log" 2>&1 &
HTTP_PID=$!

ready=0
for _ in $(seq 1 50); do
    if curl -fsS http://127.0.0.1:8099/live.m3u8 >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 0.1
done
if [ "$ready" -ne 1 ]; then
    echo "fixture HTTP server did not become ready" >&2
    exit 1
fi

declare -A PROBE_STATUS
declare -A GATE_STATUS

for encoder in libx264 vaapi nvenc; do
    if probe_encoder "$encoder"; then
        probe_status=0
    else
        probe_status=$?
    fi
    PROBE_STATUS[$encoder]=$probe_status

    if [ "${PROBE_STATUS[$encoder]}" -eq 0 ]; then
        if run_gate "$encoder"; then
            GATE_STATUS[$encoder]=0
        else
            GATE_STATUS[$encoder]=1
        fi
    else
        GATE_STATUS[$encoder]=2
    fi
done

(cd "$FIXTURE" && sha256sum p001.m3u8 p002.m3u8 init_*.mp4 p*.m4s | sort) \
    > "$WORK/source-after.sha256"
SOURCE_UNTOUCHED=0
if cmp -s "$WORK/source-before.sha256" "$WORK/source-after.sha256"; then
    SOURCE_UNTOUCHED=1
fi

overall=0
[ "${GATE_STATUS[libx264]}" -eq 0 ] || overall=1
for encoder in vaapi nvenc; do
    if [ "${PROBE_STATUS[$encoder]}" -eq 0 ] && [ "${GATE_STATUS[$encoder]}" -ne 0 ]; then
        overall=1
    fi
done
[ "$SOURCE_UNTOUCHED" -eq 1 ] || overall=1

{
    echo "# Live transcode prototype results"
    echo
    echo "- Date: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "- Requested GPU mode: \`$REQUESTED_GPU\`"
    echo "- Source files unchanged: $([ "$SOURCE_UNTOUCHED" -eq 1 ] && echo YES || echo NO)"
    echo "- Overall: $([ "$overall" -eq 0 ] && echo PASS || echo FAIL)"
    echo
    echo "| Encoder | Probe | Playlist gate | Video | Audio | Duration | Notes |"
    echo "|---|---|---|---|---|---|---|"
    for encoder in libx264 vaapi nvenc; do
        result="$RESULTS_DIR/gate-$encoder/result.txt"
        video="-"
        audio="-"
        duration="-"
        note=""
        if [ -f "$result" ]; then
            video="$(sed -n 's/^video_codec=//p' "$result")"
            audio="$(sed -n 's/^audio_codec=//p' "$result")"
            duration="$(sed -n 's/^duration=//p' "$result")"
        fi
        if [ "${PROBE_STATUS[$encoder]}" -ne 0 ]; then
            note="see probe-$encoder.log"
        elif [ "${GATE_STATUS[$encoder]}" -ne 0 ]; then
            note="see gate-$encoder/ffmpeg.log"
        fi
        echo "| $encoder | $(probe_label "${PROBE_STATUS[$encoder]}") | $(probe_label "${GATE_STATUS[$encoder]}") | $video | $audio | $duration | $note |"
    done
    echo
    echo "Logs and environment: this directory."
} > "$RESULTS_DIR/summary.md"

cat "$RESULTS_DIR/summary.md"
exit "$overall"
