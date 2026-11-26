#!/usr/bin/env bash
# Build the deploy image from a clean commit: publish the apps and server, assemble the context, docker build, save a tar.
#   deploy/build-image.sh            -> $MUDMAPS_ARTIFACTS_DIR/mud-maps-image/<sha12>-g0.9.108/ and mudmaps:<sha12>-g0.9.108
#   (MUDMAPS_ARTIFACTS_DIR defaults to ../artifacts beside the repository; MUDMAPS_IMAGE_OUT overrides the whole path)
# Never pushes; the tar is shipped to the host by the deploy step.
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
cd "$repo"
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then echo "working tree has uncommitted changes; commit first" >&2; exit 1; fi
sha=$(git rev-parse HEAD); tag="mudmaps:${sha:0:12}-g0.9.108"
out=${MUDMAPS_IMAGE_OUT:-${MUDMAPS_ARTIFACTS_DIR:-$repo/../artifacts}/mud-maps-image/${sha:0:12}-g0.9.108}
rm -rf "$out"; mkdir -p "$out/graphify"
MUDMAPS_OUT="$out" MUDMAPS_PATH_BASE= tools/publish.sh > "$out/publish.log" 2>&1
cp deploy/graphify/run.py deploy/graphify/requirements.lock "$out/graphify/"
cp deploy/Dockerfile "$out/Dockerfile"
echo "$sha" > "$out/COMMIT"
docker build -t "$tag" -f "$out/Dockerfile" "$out"
docker save "$tag" | zstd -q -T0 -19 -o "$out/mudmaps-${sha:0:12}-g0.9.108.tar.zst"
docker image inspect --format '{{.Id}}' "$tag" > "$out/IMAGE_ID"
sha256sum "$out/mudmaps-${sha:0:12}-g0.9.108.tar.zst" > "$out/SHA256SUMS"
echo "built $tag ($(cat "$out/IMAGE_ID")) -> $out"
