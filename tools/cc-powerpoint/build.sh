#!/usr/bin/env bash
# Build script for cc-powerpoint executable
# Usage: bash build.sh

set -e

echo "Building cc-powerpoint executable..."

# Check for Python
if ! command -v python &> /dev/null; then
    echo "ERROR: Python not found. Please install Python 3.11 or later."
    exit 1
fi

# Create virtual environment if it doesn't exist
if [ ! -d "venv" ]; then
    echo "Creating virtual environment..."
    python -m venv venv
fi

# Activate virtual environment
echo "Activating virtual environment..."
source venv/Scripts/activate || source venv/bin/activate

# Install dependencies
echo "Installing dependencies..."
pip install -e ".[dev]"

# Build with PyInstaller
echo "Building executable with PyInstaller..."
pyinstaller cc-powerpoint.spec --clean --noconfirm

# Check result
if [ -f "dist/cc-powerpoint.exe" ]; then
    size=$(du -h dist/cc-powerpoint.exe | cut -f1)
    echo "SUCCESS: Built dist/cc-powerpoint.exe ($size)"
elif [ -f "dist/cc-powerpoint" ]; then
    size=$(du -h dist/cc-powerpoint | cut -f1)
    echo "SUCCESS: Built dist/cc-powerpoint ($size)"
else
    echo "ERROR: Build failed - executable not found"
    exit 1
fi
