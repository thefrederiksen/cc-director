#!/usr/bin/env bash
# macOS counterpart of scripts/local-build/_local_build1.bat
# Builds CC Director (Avalonia) into local_builds/mac/cc-director-mac1
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$SCRIPT_DIR/../../local-build-mac.sh" --slot 1 "$@"
echo ""
echo "Exe location: $SCRIPT_DIR/../../../local_builds/mac/cc-director-mac1"
