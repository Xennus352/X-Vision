#!/bin/bash
# Copies your own videos into Assets/StreamingAssets so local builds bundle them. Git ignores them
# (see .gitignore): only the two small placeholder clips are in the repository. The build scripts
# run this first. Videos come from CARDBOARDXR_VIDEOS, by default a "360" folder beside the project.
# Up to 4320 wide (native 4K) they're copied as they are; wider ones (8K, too much for the phones'
# decoders) are scaled to 4K at 80 Mbit/s. Name tags set the format (VideoFormat.FromFileName).
#   bash Tools/make_videos.sh            # copies what's missing
#   bash Tools/make_videos.sh --force    # copies them all again
set -euo pipefail
P=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
SOURCE=${CARDBOARDXR_VIDEOS:-$(dirname "$P")/360}
DEST=$P/Assets/StreamingAssets
FORCE=0
[ "${1:-}" = "--force" ] && FORCE=1
[ -d "$SOURCE" ] || exit 0
mkdir -p "$DEST"
shopt -s nullglob
# The hardware encoder where there is one (macOS); libx265 elsewhere. Override with CARDBOARDXR_ENCODER.
if [ -z "${CARDBOARDXR_ENCODER:-}" ]; then
    if [ "$(uname -s)" = "Darwin" ]; then CARDBOARDXR_ENCODER=hevc_videotoolbox; else CARDBOARDXR_ENCODER=libx265; fi
fi

for input in "$SOURCE"/*.mp4 "$SOURCE"/*.mov "$SOURCE"/*.m4v; do
    base=$(basename "$input")
    output=$DEST/$base
    if [ -f "$output" ] && [ "$FORCE" = 0 ]; then
        continue
    fi
    # Just the number: csv output adds a comma when the stream carries side data (spherical metadata).
    width=$(ffprobe -v error -select_streams v:0 -show_entries stream=width -of default=nw=1:nk=1 "$input" | head -1)
    if ! [[ "$width" =~ ^[0-9]+$ ]]; then
        echo "!! ffprobe could not read $base; skipping it" >&2
        continue
    fi
    if [ "$width" -le 4320 ]; then
        echo "== copying $base into StreamingAssets"
        cp "$input" "$output.part"
    else
        echo "== scaling $base ($width wide) to 4K in StreamingAssets with $CARDBOARDXR_ENCODER"
        # The hardware encoder on macOS; libx265 elsewhere. hvc1 so Apple's players take it, the
        # index first so it opens at once (the tag is valid for any HEVC-in-MP4).
        ffmpeg -v error -stats -y -i "$input" -vf "scale=3840:1920:flags=lanczos,format=yuv420p" \
            -c:v "$CARDBOARDXR_ENCODER" -b:v 80M -tag:v hvc1 -c:a copy -movflags +faststart -f mp4 "$output.part"
    fi
    mv "$output.part" "$output"
done
