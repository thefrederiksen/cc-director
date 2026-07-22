#!/usr/bin/env bash
#
# mac-setup.sh — One-time setup for CC Director on macOS.
#
# After this runs you'll have, in "~/Applications/DevThrottle Dev" (never
# /Applications - the real installer purges stale product bundles there):
#   • "Director Dev" — your stable copy, pinned to the Dock.
#   • "Director Dev 1".."Director Dev 4" — normal-work slots
#     (find them in Launchpad or Spotlight).
#   • "Director Dev 5" — the testing slot, started and stopped freely.
#
# Re-running is also the MIGRATION path: old "CC Director*" wrappers this setup
# used to place in /Applications are removed (wrappers only - a real installed
# Director.app is never touched).
#
# Re-running is safe (idempotent). Requires the .NET 10 SDK (see scripts/local-build/mac/README.md).
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Remove the legacy in-repo bundle from the old flow — it shares the main
# bundle id and would confuse LaunchServices/Dock about which app to open.
rm -rf "$REPO_ROOT/local_builds/mac/CC Director.app"

echo "Setting up Director Dev apps (1 main + 5 slots)..."

# 0) Create and trust the stable local code-signing certificate (idempotent;
#    may ask for your administrator password). Without it every rebuild
#    re-triggers the macOS privacy popups ("... would like to access files in
#    your Desktop folder"), which stall unattended Directors.
"$SCRIPT_DIR/local-build/mac/make-signing-certificate.sh" || \
    echo "WARNING: signing certificate not set up - privacy popups will return after every rebuild."

# 1) Lay down all six app icons first (instant; slots have no binary yet).
"$SCRIPT_DIR/mac-rebuild.sh" apps

# 2) Build the main copy and pin it to the Dock.
"$SCRIPT_DIR/mac-rebuild.sh" main

cat <<'EOF'

✅ Done.

  • "Director Dev" is now in your Dock (bottom toolbar) and in
    "~/Applications/DevThrottle Dev". Click the Dock icon any time — this is
    your everyday copy.

  • Slots "Director Dev 1" … "Director Dev 4" (normal work) and "Director Dev 5"
    (testing) are in "~/Applications/DevThrottle Dev". Find them with Spotlight
    (press Cmd+Space, type "Director Dev 2") or in Launchpad. A slot that isn't
    built yet tells you how to build it when you click it.

Everyday commands (run from the repo root):

    scripts/mac-rebuild.sh main     # rebuild your stable copy
    scripts/mac-rebuild.sh 2        # build test slot 2, then open it to test
    scripts/mac-rebuild.sh all      # rebuild everything

Each app launches under launchd, so the Terminal/Wingman tabs work correctly.

To stop the macOS privacy popups from ever interrupting unattended work, grant
the slot binaries Full Disk Access once: System Settings → Privacy & Security →
Full Disk Access → press the plus button → press Command+Shift+G → go to
<repo>/local_builds/mac → add each cc-director-mac binary. Thanks to the
signing certificate created above, that grant survives rebuilds.
EOF
