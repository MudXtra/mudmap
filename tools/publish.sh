#!/usr/bin/env bash
# Publish the four map apps and the service.
#   $MUDMAPS_OUT/<project>/wwwroot   standalone copy of each app, served at "/"
#   $MUDMAPS_OUT/site/<segment>      served by MudMaps.Server under $MUDMAPS_PATH_BASE/<segment>/
#   $MUDMAPS_OUT/server              the service itself
# MUDMAPS_OUT defaults to ./artifacts; MUDMAPS_PATH_BASE (e.g. /mudmap) must match the service's Maps:PathBase.
set -euo pipefail
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet} DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export PATH=$DOTNET_ROOT:$PATH

repo=$(cd "$(dirname "$0")/.." && pwd)
out=${MUDMAPS_OUT:-$repo/artifacts}
base=${MUDMAPS_PATH_BASE:-}
base=${base%/}
if [[ -n "$base" && ! "$base" =~ ^/[a-z0-9/-]+$ ]]; then echo "MUDMAPS_PATH_BASE must look like /mudmap" >&2; exit 1; fi

# project:segment. The Stars app is served as "space".
for pair in colony:colony city:city islands:islands stars:space; do
  app=${pair%%:*} segment=${pair##*:}
  project="$repo/src/MudMaps.${app^}"
  dotnet publish "$project" -c Release -o "$out/$app" -nologo -v q
  rm -rf "$out/site/$segment.new"
  mkdir -p "$out/site"
  cp -a "$out/$app/wwwroot" "$out/site/$segment.new"
  sed -i "s#<base href=\"/\" />#<base href=\"$base/$segment/\" />#" "$out/site/$segment.new/index.html"
  rm -f "$out/site/$segment.new/index.html.gz" "$out/site/$segment.new/index.html.br"
  grep -q "<base href=\"$base/$segment/\" />" "$out/site/$segment.new/index.html"
  rm -rf "$out/site/$segment"
  mv "$out/site/$segment.new" "$out/site/$segment"
done

# Self-contained so the server carries its own patched runtime (Sentinel 02007, B1), not the host's shared framework.
# Built beside the live copy and checked before the swap, so a failed check leaves the running build alone.
rm -rf "$out/server.new"
dotnet publish "$repo/src/MudMaps.Server" -c Release -r linux-x64 --self-contained -o "$out/server.new" -nologo -v q
runtime=$(grep -o '"runtimepack.Microsoft.AspNetCore.App.Runtime.linux-x64/[0-9.]*' "$out/server.new/MudMaps.Server.deps.json" | head -1 | sed 's#.*/##')
echo "server runtime: ${runtime:-unknown}"
IFS=. read -r ma mi pa <<<"${runtime:-0.0.0}"
if [[ ! "$ma.$mi.$pa" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || (( ma < 10 || (ma == 10 && mi == 0 && pa < 15) )); then
  echo "server runtime below 10.0.15" >&2; exit 1
fi
rm -rf "$out/server.old"
if [[ -d "$out/server" ]]; then mv "$out/server" "$out/server.old"; fi
mv "$out/server.new" "$out/server"
echo "restart the service to pick it up: systemctl --user restart mudmaps-server"
echo "published to $out"
