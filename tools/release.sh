#!/usr/bin/env bash
# Usage: tools/release.sh <installer.exe> [notes-file]
# 1. GitHub release v<version> with the installer + .sha256
# 2. latest.json in the repo (version, file, sha256, size, notes, mirrors) → pushed, jsDelivr cache purged.
#    The app falls back to latest.json via jsDelivr when api.github.com is unreachable, and takes its
#    download mirror list from it (edit "mirrors" + push + purge to swap a dead mirror without a release).
set -euo pipefail
exe="$1"; notes="${2:-}"
root=$(cd "$(dirname "$0")/.." && pwd)
name=$(basename "$exe")
ver=${name#AgosClip-Setup-}; ver=${ver%.exe}
dir=$(dirname "$exe")
(cd "$dir" && sha256sum "$name" > "$name.sha256")
sha=$(cut -d' ' -f1 "$dir/$name.sha256")
size=$(stat -c %s "$exe")
args=(--repo Amigosazul/AgosClip --title "AgosClip $ver")
if [[ -n "$notes" ]]; then args+=(--notes-file "$notes"); else args+=(--notes "AgosClip $ver"); fi
gh release create "v$ver" "$exe" "$exe.sha256" "${args[@]}"

python3 - "$root/latest.json" "$ver" "$name" "$sha" "$size" "${notes:-}" <<'PY'
import json, sys
path, ver, name, sha, size, notes = sys.argv[1:7]
body = open(notes, encoding="utf-8").read().strip() if notes else f"AgosClip {ver}"
try:
    mirrors = json.load(open(path, encoding="utf-8")).get("mirrors")
except Exception:
    mirrors = None
json.dump({"version": ver, "file": name, "sha256": sha, "size": int(size), "notes": body,
           "mirrors": mirrors or ["https://ghfast.top/", "https://gh-proxy.com/", "https://ghproxy.net/"]},
          open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
git -C "$root" add latest.json
git -C "$root" commit -q -m "latest.json: $ver" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git -C "$root" push -q
curl -fsS "https://purge.jsdelivr.net/gh/Amigosazul/AgosClip@main/latest.json" > /dev/null && echo "jsDelivr purged"
