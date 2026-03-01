#!/bin/bash
# Build script for cc-markdown executable
# Usage: ./build.sh

set -e

echo "Building cc-markdown executable..."

# Check for Python
if ! command -v python3 &> /dev/null; then
    echo "ERROR: Python3 not found. Please install Python 3.11 or later."
    exit 1
fi

# Create virtual environment if it doesn't exist
if [ ! -d "venv" ]; then
    echo "Creating virtual environment..."
    python3 -m venv venv
fi

# Activate virtual environment
echo "Activating virtual environment..."
source venv/bin/activate

# Install dependencies
echo "Installing dependencies..."
pip install -e ".[dev]"

# Build with PyInstaller
echo "Building executable with PyInstaller..."
pyinstaller cc-markdown.spec --clean --noconfirm

# Check result
if [ -f "dist/cc-markdown" ]; then
    size=$(du -h dist/cc-markdown | cut -f1)
    echo "SUCCESS: Built dist/cc-markdown ($size)"
else
    echo "ERROR: Build failed - executable not found"
    exit 1
fi
