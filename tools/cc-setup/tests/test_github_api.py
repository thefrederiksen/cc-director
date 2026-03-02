"""Tests for cc-setup GitHub API module."""

import json
import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock
from urllib.error import HTTPError, URLError

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

import github_api


class TestConstants:
    """Tests for module constants."""

    def test_api_base_url(self):
        """API base URL is correct."""
        assert "api.github.com" in github_api.GITHUB_API_BASE

    def test_repo_owner(self):
        """Repository owner is set."""
        assert github_api.REPO_OWNER == "thefrederiksen"

    def test_repo_name(self):
        """Repository name is set."""
        assert github_api.REPO_NAME == "cc-director"


class TestGetLatestRelease:
    """Tests for get_latest_release function."""

    def test_returns_none_on_404(self):
        """Returns None when no releases exist (404)."""
        mock_error = HTTPError(
            url="https://api.github.com/repos/test/test/releases/latest",
            code=404,
            msg="Not Found",
            hdrs={},
            fp=None,
        )
        with patch("urllib.request.urlopen", side_effect=mock_error):
            result = github_api.get_latest_release()
        assert result is None

    def test_raises_on_other_http_error(self):
        """Re-raises non-404 HTTP errors."""
        mock_error = HTTPError(
            url="https://api.github.com/repos/test/test/releases/latest",
            code=500,
            msg="Server Error",
            hdrs={},
            fp=None,
        )
        with patch("urllib.request.urlopen", side_effect=mock_error):
            with pytest.raises(HTTPError):
                github_api.get_latest_release()

    def test_returns_release_data(self):
        """Returns parsed release data on success."""
        release_data = {
            "tag_name": "v1.0.0",
            "name": "Release 1.0.0",
            "assets": [],
        }
        mock_response = MagicMock()
        mock_response.read.return_value = json.dumps(release_data).encode("utf-8")
        mock_response.__enter__ = lambda s: s
        mock_response.__exit__ = MagicMock(return_value=False)

        with patch("urllib.request.urlopen", return_value=mock_response):
            result = github_api.get_latest_release()

        assert result is not None
        assert result["tag_name"] == "v1.0.0"


class TestGetReleaseAssets:
    """Tests for get_release_assets function."""

    def test_extracts_assets(self):
        """Extracts asset name-to-URL mapping."""
        release = {
            "assets": [
                {
                    "name": "cc-tool.exe",
                    "browser_download_url": "https://example.com/cc-tool.exe",
                },
                {
                    "name": "cc-other.exe",
                    "browser_download_url": "https://example.com/cc-other.exe",
                },
            ]
        }
        assets = github_api.get_release_assets(release)
        assert len(assets) == 2
        assert "cc-tool.exe" in assets
        assert assets["cc-tool.exe"] == "https://example.com/cc-tool.exe"

    def test_empty_assets(self):
        """Returns empty dict when no assets."""
        release = {"assets": []}
        assets = github_api.get_release_assets(release)
        assert assets == {}

    def test_missing_assets_key(self):
        """Returns empty dict when assets key missing."""
        release = {}
        assets = github_api.get_release_assets(release)
        assert assets == {}

    def test_skips_entries_without_name(self):
        """Skips assets missing name or URL."""
        release = {
            "assets": [
                {"name": "", "browser_download_url": "https://example.com/a.exe"},
                {"name": "b.exe", "browser_download_url": ""},
            ]
        }
        assets = github_api.get_release_assets(release)
        assert assets == {}


class TestDownloadFile:
    """Tests for download_file function."""

    def test_download_success(self, tmp_path):
        """Successful download returns True."""
        dest = str(tmp_path / "test.bin")
        content = b"test content"

        mock_response = MagicMock()
        mock_response.headers = {"Content-Length": str(len(content))}
        mock_response.read.side_effect = [content, b""]
        mock_response.__enter__ = lambda s: s
        mock_response.__exit__ = MagicMock(return_value=False)

        with patch("urllib.request.urlopen", return_value=mock_response):
            result = github_api.download_file("https://example.com/file", dest, show_progress=False)

        assert result is True
        assert Path(dest).read_bytes() == content

    def test_download_network_error(self, tmp_path):
        """Network error returns False."""
        dest = str(tmp_path / "test.bin")
        with patch("urllib.request.urlopen", side_effect=URLError("Network unreachable")):
            result = github_api.download_file("https://example.com/file", dest, show_progress=False)
        assert result is False
