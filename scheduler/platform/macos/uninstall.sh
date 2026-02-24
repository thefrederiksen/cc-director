#!/bin/bash
# cc_director macOS launchd Uninstaller

set -e

PLIST_NAME="com.cc.director.plist"
PLIST_DEST="$HOME/Library/LaunchAgents/$PLIST_NAME"

echo "cc_director macOS Service Uninstaller"
echo "======================================"
echo ""

if [ ! -f "$PLIST_DEST" ]; then
    echo "Service is not installed."
    exit 0
fi

echo "Unloading service..."
launchctl unload "$PLIST_DEST" 2>/dev/null || true

echo "Removing plist..."
rm -f "$PLIST_DEST"

echo ""
echo "Service uninstalled."
