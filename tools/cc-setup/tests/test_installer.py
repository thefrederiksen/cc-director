"""Tests for cc-setup installer module."""

import os
import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from installer import CCDirectorInstaller


class TestCCDirectorInstaller:
    """Tests for the main installer class."""

    def test_install_dir_uses_localappdata(self, tmp_path):
        """Install directory resolves to LOCALAPPDATA/cc-director."""
        with patch.dict(os.environ, {"LOCALAPPDATA": str(tmp_path)}):
            installer = CCDirectorInstaller()
        assert "cc-director" in str(installer.install_dir)

    def test_install_dir_is_path(self):
        """Install directory is a Path object."""
        installer = CCDirectorInstaller()
        assert isinstance(installer.install_dir, Path)

    def test_skill_dir_is_path(self):
        """Skill directory is a Path object."""
        installer = CCDirectorInstaller()
        assert isinstance(installer.skill_dir, Path)


class TestInstallerPaths:
    """Tests for path resolution."""

    def test_skill_install_path(self):
        """Claude Code skill installs to ~/.claude/skills/cc-director/."""
        installer = CCDirectorInstaller()
        assert "skills" in str(installer.skill_dir)
        assert "cc-director" in str(installer.skill_dir)

    def test_install_dir_contains_cc_director(self, tmp_path):
        """Install dir path includes cc-director."""
        with patch.dict(os.environ, {"LOCALAPPDATA": str(tmp_path)}):
            installer = CCDirectorInstaller()
        assert installer.install_dir == tmp_path / "cc-director"
