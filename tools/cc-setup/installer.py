"""
Core installer logic for cc-director tools.
"""

import os
import sys
import winreg
from pathlib import Path
from typing import List, Tuple

from github_api import (
    get_latest_release,
    get_release_assets,
    download_file,
    download_raw_file
)


# Tools to install (order matters for display)
TOOLS = [
    "cc-markdown",
    "cc-transcribe",
    "cc-image",
    "cc-voice",
    "cc-whisper",
    "cc-video",
]


class CCToolsInstaller:
    """Installer for cc-tools suite."""

    def __init__(self):
        self.install_dir = Path(os.environ.get("LOCALAPPDATA", "")) / "cc-director"
        self.skill_dir = Path(os.environ.get("USERPROFILE", "")) / ".claude" / "skills" / "cc-tools"

    def install(self) -> bool:
        """
        Run the full installation.

        Returns:
            True if successful, False otherwise
        """
        # Step 1: Create install directory
        print(f"[1/5] Creating install directory...")
        print(f"      {self.install_dir}")
        self.install_dir.mkdir(parents=True, exist_ok=True)

        # Step 2: Get latest release info
        print(f"[2/5] Checking for latest release...")
        release = get_latest_release()

        if release:
            version = release.get("tag_name", "unknown")
            print(f"      Found release: {version}")
            assets = get_release_assets(release)

            # Step 3: Download tools
            print(f"[3/5] Downloading tools...")
            downloaded, skipped = self._download_tools(assets)
            print(f"      Downloaded: {downloaded}, Skipped (not yet released): {skipped}")
        else:
            print("      No releases found. Skipping tool downloads.")
            print("      (Tools will be available after first release)")

        # Step 4: Add to PATH
        print(f"[4/5] Configuring PATH...")
        if self._add_to_path():
            print(f"      Added {self.install_dir} to user PATH")
        else:
            print(f"      Already in PATH")

        # Step 5: Install SKILL.md
        print(f"[5/5] Installing Claude Code skill...")
        self.skill_dir.mkdir(parents=True, exist_ok=True)
        skill_path = self.skill_dir / "SKILL.md"

        if download_raw_file("skills/cc-tools/SKILL.md", str(skill_path)):
            print(f"      Installed: {skill_path}")
        else:
            print(f"      WARNING: Could not download SKILL.md")
            print(f"      Claude Code integration may not work until manually installed.")

        return True

    def _download_tools(self, assets: dict) -> Tuple[int, int]:
        """
        Download tool executables from release assets.

        Args:
            assets: Dict mapping asset names to download URLs

        Returns:
            Tuple of (downloaded_count, skipped_count)
        """
        downloaded = 0
        skipped = 0

        for tool in TOOLS:
            asset_name = f"{tool}-windows-x64.exe"

            if asset_name not in assets:
                skipped += 1
                continue

            dest_path = self.install_dir / f"{tool}.exe"
            print(f"      Downloading {tool}.exe...")

            if download_file(assets[asset_name], str(dest_path)):
                downloaded += 1
            else:
                print(f"      WARNING: Failed to download {tool}")

        return downloaded, skipped

    def _add_to_path(self) -> bool:
        """
        Add install directory to user PATH environment variable.

        Returns:
            True if PATH was modified, False if already present
        """
        install_str = str(self.install_dir)

        try:
            # Open user environment variables
            key = winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Environment",
                0,
                winreg.KEY_READ | winreg.KEY_WRITE
            )

            try:
                # Get current PATH
                current_path, _ = winreg.QueryValueEx(key, "Path")
            except WindowsError:
                current_path = ""

            # Check if already in PATH (case-insensitive)
            path_entries = [p.strip() for p in current_path.split(";") if p.strip()]
            path_lower = [p.lower() for p in path_entries]

            if install_str.lower() in path_lower:
                winreg.CloseKey(key)
                return False

            # Add to PATH
            new_path = current_path.rstrip(";") + ";" + install_str if current_path else install_str
            winreg.SetValueEx(key, "Path", 0, winreg.REG_EXPAND_SZ, new_path)
            winreg.CloseKey(key)

            # Notify Windows of environment change
            try:
                import ctypes
                HWND_BROADCAST = 0xFFFF
                WM_SETTINGCHANGE = 0x1A
                SMTO_ABORTIFHUNG = 0x0002
                result = ctypes.c_long()
                ctypes.windll.user32.SendMessageTimeoutW(
                    HWND_BROADCAST,
                    WM_SETTINGCHANGE,
                    0,
                    "Environment",
                    SMTO_ABORTIFHUNG,
                    5000,
                    ctypes.byref(result)
                )
            except OSError:
                pass  # Non-critical if broadcast fails

            return True

        except OSError as e:
            print(f"      WARNING: Could not modify PATH: {e}")
            print(f"      Please manually add {install_str} to your PATH")
            return False
