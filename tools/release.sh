#!/usr/bin/env bash
# Usage: tools/release.sh <installer.exe> [notes-file]
# Creates GitHub release v<version> on Amigosazul/AgosClip with the installer + .sha256.
set -euo pipefail
exe="$1"; notes="${2:-}"
name=$(basename "$exe")
ver=${name#AgosClip-Setup-}; ver=${ver%.exe}
dir=$(dirname "$exe")
(cd "$dir" && sha256sum "$name" > "$name.sha256")
args=(--repo Amigosazul/AgosClip --title "AgosClip $ver")
if [[ -n "$notes" ]]; then args+=(--notes-file "$notes"); else args+=(--notes "AgosClip $ver"); fi
gh release create "v$ver" "$exe" "$exe.sha256" "${args[@]}"
