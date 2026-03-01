"""Tests for cc-spotify CLI."""

import json
import pytest
from unittest.mock import patch, MagicMock
from typer.testing import CliRunner

from src.cli import app, parse_js_result, _find_testid_ref, _find_text_ref


runner = CliRunner()


# =============================================================================
# parse_js_result
# =============================================================================

class TestParseJsResult:
    def test_parse_json_string(self):
        result = {"result": '{"name": "Song", "artist": "Artist"}'}
        data = parse_js_result(result)
        assert data["name"] == "Song"
        assert data["artist"] == "Artist"

    def test_parse_dict_result(self):
        result = {"result": {"name": "Song"}}
        data = parse_js_result(result)
        assert data["name"] == "Song"

    def test_parse_value_key(self):
        result = {"value": '{"name": "Test"}'}
        data = parse_js_result(result)
        assert data["name"] == "Test"

    def test_parse_invalid_json(self):
        result = {"result": "not json"}
        data = parse_js_result(result)
        assert data["raw"] == "not json"

    def test_parse_empty(self):
        result = {}
        data = parse_js_result(result)
        assert data == {}

    def test_parse_non_string_non_dict(self):
        result = {"result": 42}
        data = parse_js_result(result)
        assert data["raw"] == "42"


# =============================================================================
# Snapshot Search Helpers
# =============================================================================

class TestSnapshotHelpers:
    def test_find_testid_ref_simple(self):
        snapshot = {
            "children": [
                {"ref": "e1", "attributes": {"data-testid": "control-button-shuffle"}},
                {"ref": "e2", "attributes": {"data-testid": "control-button-repeat"}},
            ]
        }
        assert _find_testid_ref(snapshot, "control-button-shuffle") == "e1"
        assert _find_testid_ref(snapshot, "control-button-repeat") == "e2"
        assert _find_testid_ref(snapshot, "nonexistent") is None

    def test_find_testid_ref_nested(self):
        snapshot = {
            "children": [
                {
                    "ref": "parent",
                    "attributes": {},
                    "children": [
                        {"ref": "deep", "attributes": {"data-testid": "target"}}
                    ]
                }
            ]
        }
        assert _find_testid_ref(snapshot, "target") == "deep"

    def test_find_text_ref(self):
        snapshot = {
            "children": [
                {"ref": "t1", "text": "My Playlist"},
                {"ref": "t2", "text": "Another Playlist"},
            ]
        }
        assert _find_text_ref(snapshot, "My Playlist") == "t1"
        assert _find_text_ref(snapshot, "my playlist") == "t1"  # case insensitive
        assert _find_text_ref(snapshot, "Not Found") is None

    def test_find_text_ref_nested(self):
        snapshot = {
            "nodes": [
                {
                    "ref": "outer",
                    "text": "container",
                    "children": [
                        {"ref": "inner", "text": "Target Text Here"}
                    ]
                }
            ]
        }
        assert _find_text_ref(snapshot, "Target Text") == "inner"


# =============================================================================
# Config Command
# =============================================================================

class TestConfigCommand:
    @patch("src.cli.get_config_dir")
    def test_config_show_no_file(self, mock_dir, tmp_path):
        mock_dir.return_value = tmp_path / "spotify"
        result = runner.invoke(app, ["config", "--show"])
        assert result.exit_code == 0
        assert "No config file" in result.output

    @patch("src.cli.get_config_dir")
    def test_config_set_workspace(self, mock_dir, tmp_path):
        config_dir = tmp_path / "spotify"
        config_dir.mkdir()
        mock_dir.return_value = config_dir

        result = runner.invoke(app, ["config", "--workspace", "my-workspace"])
        assert result.exit_code == 0
        assert "my-workspace" in result.output

        # Verify config file
        config_file = config_dir / "config.json"
        assert config_file.exists()
        data = json.loads(config_file.read_text())
        assert data["default_workspace"] == "my-workspace"


# =============================================================================
# Vault Integration
# =============================================================================

class TestVaultIntegration:
    def test_parse_vault_response(self):
        from src.vault_integration import _parse_vault_response

        response = """Based on your preferences:
- Miles Davis
- Radiohead
- Khruangbin
- Jazz and ambient genres
"""
        result = _parse_vault_response(response)
        assert "Miles Davis" in result
        assert "Radiohead" in result
        assert "Khruangbin" in result

    def test_parse_vault_response_no_data(self):
        from src.vault_integration import _parse_vault_response

        response = "No music preferences found in vault."
        result = _parse_vault_response(response)
        assert result == []

    def test_parse_vault_response_with_mood(self):
        from src.vault_integration import _parse_vault_response

        response = "- Jazz\n- Blues"
        result = _parse_vault_response(response, mood="chill")
        assert any("chill" in s for s in result)
