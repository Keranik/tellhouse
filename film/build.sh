#!/usr/bin/env bash
# Build the Tellhouse film: render any story scene that is missing or out of
# date, then assemble the cut from edl.json with whatever narration (audio/)
# and footage (footage/) is present. Output: out/tellhouse.mp4.
#   ./build.sh            build
#   ./build.sh --dry      show the timeline only
set -euo pipefail
cd "$(dirname "$0")"
for tool in node python3 ffmpeg ffprobe; do
  command -v "$tool" >/dev/null || { echo "build.sh: $tool is required" >&2; exit 1; }
done
if [[ "${1:-}" != "--dry" ]]; then
  node render-scenes.mjs --missing
fi
python3 assemble.py "$@"
