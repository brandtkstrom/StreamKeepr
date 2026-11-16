#!/usr/bin/env bash
# Dev-only: builds src/StreamKeepr/wwwroot/fonts/roboto-latin.woff2 from the official
# variable Roboto (google/fonts, ofl/roboto/Roboto[wdth,wght].ttf, OFL-1.1).
#
#   scripts/build-roboto.sh [path/to/Roboto[wdth,wght].ttf]
#
# Without a path it downloads the pinned source and its licence. Needs fontTools with
# Brotli (`pip install fonttools brotli`). The output is committed; nothing here runs at
# build or runtime.
#
# The result keeps the wght axis (100-900) and drops wdth at its default (100), since the
# app never changes width. It is subset to Google Fonts' "latin" range and keeps the
# default OpenType features plus tnum, which the timecode and stats styles rely on.
set -euo pipefail

# fontTools stamps head.modified with the build time unless this is set; a fixed value keeps
# the committed woff2 reproducible byte for byte.
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-1735689600}"

SOURCE_URL="https://raw.githubusercontent.com/google/fonts/6183fc0d26361f6ddfd6f6b7a736e1467c6d8a43/ofl/roboto/Roboto%5Bwdth,wght%5D.ttf"
LICENSE_URL="https://raw.githubusercontent.com/google/fonts/6183fc0d26361f6ddfd6f6b7a736e1467c6d8a43/ofl/roboto/OFL.txt"
SOURCE_SHA256="d7598e12c5dbef095ff8272cfc55da0250bd07fbdecbac8a530b9b277872a134"

# Google Fonts' latin subset (the unicode-range it serves for Roboto's latin face).
LATIN="U+0000-00FF,U+0131,U+0152-0153,U+02BB-02BC,U+02C6,U+02DA,U+02DC,U+0304,U+0308,U+0329,U+2000-206F,U+20AC,U+2122,U+2191,U+2193,U+2212,U+2215,U+FEFF,U+FFFD"

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fonts="$repo/src/StreamKeepr/wwwroot/fonts"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

source_ttf="${1:-}"
if [[ -z "$source_ttf" ]]; then
  source_ttf="$work/Roboto[wdth,wght].ttf"
  curl --fail --silent --show-error --location --output "$source_ttf" "$SOURCE_URL"
  curl --fail --silent --show-error --location --output "$fonts/ROBOTO-LICENSE" "$LICENSE_URL"
fi

actual="$(shasum -a 256 "$source_ttf" | cut -d' ' -f1)"
if [[ "$actual" != "$SOURCE_SHA256" ]]; then
  echo "error: $source_ttf has SHA-256 $actual, expected $SOURCE_SHA256" >&2
  exit 1
fi

fonttools varLib.instancer --quiet --output "$work/roboto-wght.ttf" "$source_ttf" wdth=100
pyftsubset "$work/roboto-wght.ttf" \
  --unicodes="$LATIN" \
  --layout-features+=tnum \
  --flavor=woff2 \
  --output-file="$fonts/roboto-latin.woff2"

echo "wrote $fonts/roboto-latin.woff2 ($(wc -c < "$fonts/roboto-latin.woff2" | tr -d ' ') bytes)"
