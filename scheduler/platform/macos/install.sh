#!/bin/bash
# cc_director macOS launchd Installation

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SCHEDULER_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
PLIST_NAME="com.cc.director.plist"
PLIST_SOURCE="$SCRIPT_DIR/$PLIST_NAME"
PLIST_DEST="$HOME/Library/LaunchAgents/$PLIST_NAME"

echo "cc_director macOS Service Installer"
echo "===================================="
echo ""

# Check if already installed
if [ -f "$PLIST_DEST" ]; then
    echo "Service already installed at $PLIST_DEST"
    echo "To reinstall, first run: ./uninstall.sh"
    exit 1
fi

# Create logs directory
mkdir -p "$SCHEDULER_DIR/logs"

# Generate plist with correct paths
echo "Generating plist with paths..."
sed -e "s|/path/to/cc_director/scheduler|$SCHEDULER_DIR|g" \
    "$PLIST_SOURCE" > "$PLIST_DEST"

echo "Plist installed to: $PLIST_DEST"
echo ""

# Load the service
echo "Loading service..."
launchctl load "$PLIST_DEST"

echo ""
echo "Service installed and started."
echo ""
echo "Useful commands:"
echo "  Check status:  launchctl list | grep cc.director"
echo "  Stop service:  launchctl unload $PLIST_DEST"
echo "  Start service: launchctl load $PLIST_DEST"
echo "  View logs:     tail -f $SCHEDULER_DIR/logs/cc_director.log"
