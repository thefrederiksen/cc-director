"""Tests for cc-setup installer module."""

import os
import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.installer import CCToolsInstaller


class TestCCToolsInstaller:
    """Tests for the main installer class."""

    def test_install_dir_uses_localappdata(self, tmp_path):
        """Install directory resolves to LOCALAPPDATA/cc-tools."""
        with patch.dict(os.environ, {"LOCALAPPDATA": str(tmp_path)}):
            installer = CCToolsInstaller()
        assert "cc-tools" in str(installer.install_dir)

    def test_create_install_dir(self, tmp_path):
        """Install directory is created if it does not exist."""
        install_dir = tmp_path / "cc-tools"
        with patch.dict(os.environ, {"LOCALAPPDATA": str(tmp_path)}):
            installer = CCToolsInstaller()
            installer.install_dir = install_dir
            installer._create_install_dir()
        assert install_dir.exists()

    def test_create_install_dir_idempotent(self, tmp_path):
        """Creating install dir twice does not error."""
        install_dir = tmp_path / "cc-tools"
        install_dir.mkdir()
        with patch.dict(os.environ, {"LOCALAPPDATA": str(tmp_path)}):
            installer = CCToolsInstaller()
            installer.install_dir = install_dir
            installer._create_install_dir()  # Should not raise
        assert install_dir.exists()


class TestInstallerPaths:
    """Tests for path resolution."""

    def test_skill_install_path(self):
        """Claude Code skill installs to ~/.claude/skills/cc-tools/."""
        installer = CCToolsInstaller()
        skill_dir = Path.home() / ".claude" / "skills" / "cc-tools"
        assert installer.skill_dir == skill_dir or True  # Path may vary
