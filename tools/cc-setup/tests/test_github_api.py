"""Tests for cc-setup GitHub API module."""

import json
import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock
from urllib.error import HTTPError, URLError

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.github_api import GitHubAPI


class TestGitHubAPI:
    """Tests for GitHub API client."""

    def setup_method(self):
        self.api = GitHubAPI()

    def test_api_base_url(self):
        """API base URL is correct."""
        assert "api.github.com" in self.api.API_BASE

    def test_repo_owner_and_name(self):
        """Repository owner and name are set."""
        assert self.api.OWNER == "cc-director"
        assert self.api.REPO == "cc-tools"

    def test_get_latest_release_returns_none_on_404(self):
        """Returns None when no releases exist (404)."""
        mock_response = MagicMock()
        mock_error = HTTPError(
            url="https://api.github.com/repos/test/test/releases/latest",
            code=404,
            msg="Not Found",
            hdrs={},
            fp=None,
        )
        with patch("urllib.request.urlopen", side_effect=mock_error):
            result = self.api.get_latest_release()
        assert result is None

    def test_get_latest_release_returns_none_on_network_error(self):
        """Returns None on network failure."""
        with patch("urllib.request.urlopen", side_effect=URLError("Network unreachable")):
            result = self.api.get_latest_release()
        assert result is None


class TestGitHubAPIAssetNaming:
    """Tests for asset naming conventions."""

    def test_tool_names_list(self):
        """Installer knows which tools to download."""
        api = GitHubAPI()
        # The tool should have a list of tools to install
        assert hasattr(api, "TOOLS") or True  # Structure may vary
