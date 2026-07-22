#!/usr/bin/env bash
#
# mac-rebuild.sh — Build a CC Director target and refresh its macOS app.
#
# This is the one command you run while developing. It builds the requested
# binary (via local-build-mac.sh), then (re)creates the matching .app bundle in
# "~/Applications/DevThrottle Dev" (NEVER /Applications - the real installer
# purges stale product bundles there, and dev wrappers must be out of its
# reach; see make-app-bundle.sh). For the main target it also pins the app to
# the Dock.
#
# Targets:
#   main        Build the stable copy -> "Director Dev.app", pinned to the Dock.
#   1|2|3|4     Build a normal-work slot -> "Director Dev N.app" (run several at once).
#   5           Build the testing slot -> "Director Dev 5.app" (started and stopped freely).
#   all         Build main + slots 1 through 5.
#   apps        (Re)create all six .app bundles WITHOUT building — fast, used by
#               mac-setup.sh to lay down the icons before anything is built.
#
# Usage:
#   scripts/mac-rebuild.sh main
#   scripts/mac-rebuild.sh 2
#   scripts/mac-rebuild.sh all
#
# Env:
#   APPS_DIR    Where to install the apps (default "~/Applications/DevThrottle Dev").
#
set -euo pipefail

TARGET="${1:-}"
if [[ -z "$TARGET" ]]; then
    echo "Usage: scripts/mac-rebuild.sh <main|1|2|3|4|5|all|apps>" >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
MAC_DIR="$REPO_ROOT/local_builds/mac"
MAC_HELPERS="$REPO_ROOT/scripts/local-build/mac"
APPS_DIR="${APPS_DIR:-$HOME/Applications/DevThrottle Dev}"
export APPS_DIR

# Build one target's binary, then (re)make its app bundle.
build_one() {
    local t="$1" slot
    if [[ "$t" == "main" ]]; then slot="-main"; else slot="$t"; fi
    echo "==> Building $t ..."
    "$REPO_ROOT/scripts/local-build-mac.sh" --slot "$slot"
    "$MAC_HELPERS/make-app-bundle.sh" --target "$t"
}

# Pin the main app to the Dock. Always unpins any existing 'Director Dev' tile
# (and any old 'CC Director' tile from the pre-migration layout) first, rebuilds
# the icon cache, then re-pins — so the tile reliably shows the current icon
# even if an earlier (icon-less) bundle was cached by the Dock.
pin_dock() {
    local app="$APPS_DIR/Director Dev.app"
    touch "$app"
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
        -f "$app" 2>/dev/null || true

    # Remove any existing 'Director Dev' tile, and any old 'CC Director' tile
    # left from the pre-migration /Applications layout (its app is gone).
    python3 - <<PY 2>/dev/null || true
import subprocess, plistlib
data = subprocess.run(["defaults","export","com.apple.dock","-"],capture_output=True).stdout
pl = plistlib.loads(data)
def is_stale(e):
    try: s = e["tile-data"]["file-data"]["_CFURLString"].replace("%20"," ")
    except Exception: return False
    s = s.rstrip("/")
    return s.endswith("/Director Dev.app") or s.endswith("/CC Director.app")
pl["persistent-apps"] = [e for e in pl.get("persistent-apps",[]) if not is_stale(e)]
subprocess.run(["defaults","import","com.apple.dock","-"],input=plistlib.dumps(pl))
PY

    # Force the icon-services cache to rebuild, then re-pin fresh.
    killall iconservicesagent 2>/dev/null || true
    defaults write com.apple.dock persistent-apps -array-add \
        "<dict><key>tile-data</key><dict><key>file-data</key><dict><key>_CFURLString</key><string>file://$app/</string><key>_CFURLStringType</key><integer>15</integer></dict></dict></dict>"
    killall Dock
    echo "Dock: pinned 'Director Dev' (the Dock restarted briefly — that's normal)."
}

case "$TARGET" in
    main)
        build_one main
        pin_dock ;;
    1|2|3|4|5)
        build_one "$TARGET"
        echo "Open it from Launchpad/Spotlight, or: open \"$APPS_DIR/Director Dev $TARGET.app\"" ;;
    all)
        build_one main
        for n in 1 2 3 4 5; do build_one "$n"; done
        pin_dock ;;
    apps)
        for t in main 1 2 3 4 5; do "$MAC_HELPERS/make-app-bundle.sh" --target "$t"; done ;;
    *)
        echo "ERROR: invalid target '$TARGET' (use: main|1|2|3|4|5|all|apps)" >&2
        exit 1 ;;
esac
