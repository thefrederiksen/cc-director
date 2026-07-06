#!/usr/bin/env bash
# Launches the built cc-director from a terminal with the correct .NET runtime
# environment. The framework-dependent build needs DOTNET_ROOT pointed at the
# SDK installed under ~/.dotnet (Finder/your shell don't set this by default).
#
# Usage:  ./scripts/local-build/mac/run.sh [--slot N] [-- <app args>]
set -euo pipefail

SLOT="1"
if [[ "${1:-}" == "--slot" ]]; then SLOT="$2"; shift 2; fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
BIN_DIR="$REPO_ROOT/local_builds/mac"
BIN="$BIN_DIR/cc-director-mac${SLOT}"

if [[ ! -x "$BIN" ]]; then
    echo "ERROR: $BIN not found. Build it first: ./scripts/local-build/mac/_local_build_mac${SLOT}.sh" >&2
    exit 1
fi

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
cd "$BIN_DIR"
exec "$BIN" "$@"
