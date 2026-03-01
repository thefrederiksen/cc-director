"""Entry point for cc-personresearch when run as executable."""

import sys
import os

# Add the current directory to path for imports
if getattr(sys, 'frozen', False):
    # Running as PyInstaller bundle
    app_dir = os.path.dirname(sys.executable)
    if app_dir not in sys.path:
        sys.path.insert(0, app_dir)
else:
    # Running as script
    app_dir = os.path.dirname(os.path.abspath(__file__))
    if app_dir not in sys.path:
        sys.path.insert(0, app_dir)

from src.cli import app

if __name__ == "__main__":
    app()
