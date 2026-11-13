#!/usr/bin/env python3
"""Disposable live-playlist stitcher for the #147 prototype.

Mirrors the part-header stripping and discontinuity insertion in
src/StreamKeepr/Infrastructure/Storage/PlaylistStitcher.cs. It is not the
production stitcher and must not be used outside this prototype.
"""

from __future__ import annotations

import os
import sys
import tempfile

HEADER_PREFIXES = (
    "#EXTM3U",
    "#EXT-X-VERSION",
    "#EXT-X-TARGETDURATION",
    "#EXT-X-MEDIA-SEQUENCE",
    "#EXT-X-PLAYLIST-TYPE",
    "#EXT-X-INDEPENDENT-SEGMENTS",
    "#EXT-X-ENDLIST",
)


def body_lines(part: str) -> list[str]:
    lines: list[str] = []
    with open(part, encoding="utf-8") as handle:
        for raw in handle:
            line = raw.strip()
            if not line or line.startswith(HEADER_PREFIXES):
                continue
            lines.append(line)

    # Keep everything through the last segment URI. This drops a half-written
    # trailing #EXTINF, matching PlaylistStitcher.BodyLines.
    last_segment = -1
    for index, line in enumerate(lines):
        if not line.startswith("#"):
            last_segment = index
    return lines[: last_segment + 1]


def write_atomic(path: str, lines: list[str]) -> None:
    directory = os.path.dirname(path) or "."
    fd, temp = tempfile.mkstemp(prefix=".live-", dir=directory)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            for line in lines:
                handle.write(line)
                handle.write("\n")
        os.replace(temp, path)
    except BaseException:
        try:
            os.unlink(temp)
        except FileNotFoundError:
            pass
        raise


def init(live: str, part: str) -> None:
    lines = [
        "#EXTM3U",
        "#EXT-X-VERSION:7",
        "#EXT-X-TARGETDURATION:4",
        "#EXT-X-MEDIA-SEQUENCE:0",
        "#EXT-X-PLAYLIST-TYPE:EVENT",
        "#EXT-X-INDEPENDENT-SEGMENTS",
    ]
    lines.extend(body_lines(part))
    write_atomic(live, lines)


def append(live: str, part: str) -> None:
    with open(live, encoding="utf-8") as handle:
        lines = [line.rstrip("\n") for line in handle if line.strip()]
    if "#EXT-X-ENDLIST" in lines:
        raise SystemExit("cannot append to a finalized playlist")
    lines.append("#EXT-X-DISCONTINUITY")
    lines.extend(body_lines(part))
    write_atomic(live, lines)


def main() -> int:
    if len(sys.argv) != 4 or sys.argv[1] not in {"init", "append"}:
        print(f"usage: {sys.argv[0]} init|append LIVE PART", file=sys.stderr)
        return 2
    command, live, part = sys.argv[1], sys.argv[2], sys.argv[3]
    if command == "init":
        init(live, part)
    else:
        append(live, part)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
