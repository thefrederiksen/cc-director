"""Tests for cc-settings settings service."""

import json
import os
import pytest
from pathlib import Path
from unittest.mock import patch

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.settings import (
    get_all_settings,
    get_section,
    get_section_names,
    get_value,
    list_keys,
    set_value,
)
from cc_shared.config import CCDirectorConfig


class TestGetAllSettings:
    """Tests for get_all_settings."""

    def test_returns_flat_dict(self):
        """All settings are flattened with dotted keys."""
        config = CCDirectorConfig()
        settings = get_all_settings(config)
        assert isinstance(settings, dict)
        # Should have dotted keys
        assert "llm.default_provider" in settings
        assert "vault.vault_path" in settings
        assert "screenshots.source_directory" in settings

    def test_no_nested_dicts_in_values(self):
        """Flattened values should not contain nested dicts."""
        config = CCDirectorConfig()
        settings = get_all_settings(config)
        for key, value in settings.items():
            assert not isinstance(value, dict), f"Key {key} has dict value"


class TestGetSection:
    """Tests for get_section."""

    def test_known_section(self):
        """Getting a known section returns its data."""
        config = CCDirectorConfig()
        section = get_section(config, "screenshots")
        assert section is not None
        assert "source_directory" in section

    def test_unknown_section(self):
        """Getting an unknown section returns None."""
        config = CCDirectorConfig()
        section = get_section(config, "nonexistent")
        assert section is None

    def test_all_sections_accessible(self):
        """All sections from to_dict() are accessible."""
        config = CCDirectorConfig()
        for name in config.to_dict():
            section = get_section(config, name)
            assert section is not None, f"Section {name} not accessible"


class TestGetValue:
    """Tests for get_value."""

    def test_known_key(self):
        """Getting a known key returns (True, value)."""
        config = CCDirectorConfig()
        found, value = get_value(config, "llm.default_provider")
        assert found is True
        assert value == "claude_code"

    def test_unknown_key(self):
        """Getting an unknown key returns (False, None)."""
        config = CCDirectorConfig()
        found, value = get_value(config, "nonexistent.key")
        assert found is False
        assert value is None

    def test_screenshots_key(self):
        """Getting screenshots.source_directory works."""
        config = CCDirectorConfig()
        found, value = get_value(config, "screenshots.source_directory")
        assert found is True
        assert isinstance(value, str)


class TestSetValue:
    """Tests for set_value."""

    def test_set_string_value(self, tmp_path):
        """Setting a string value works and persists."""
        config_file = tmp_path / "config.json"
        config = CCDirectorConfig()
        config._config_path = config_file

        success = set_value(config, "screenshots.source_directory", "D:/New/Path")
        assert success is True
        assert config.screenshots.source_directory == "D:/New/Path"

        # Verify it was saved
        saved = json.loads(config_file.read_text())
        assert saved["screenshots"]["source_directory"] == "D:/New/Path"

    def test_set_bool_value(self, tmp_path):
        """Setting a bool value coerces correctly."""
        config_file = tmp_path / "config.json"
        config = CCDirectorConfig()
        config._config_path = config_file

        success = set_value(
            config, "llm.providers.claude_code.enabled", "false"
        )
        assert success is True
        assert config.llm.providers.claude_code.enabled is False

    def test_set_unknown_key(self, tmp_path):
        """Setting an unknown key returns False."""
        config = CCDirectorConfig()
        config._config_path = tmp_path / "config.json"
        success = set_value(config, "fake.section.key", "value")
        assert success is False

    def test_set_single_segment_key(self, tmp_path):
        """Setting a key with no dots returns False."""
        config = CCDirectorConfig()
        config._config_path = tmp_path / "config.json"
        success = set_value(config, "nodots", "value")
        assert success is False


class TestListKeys:
    """Tests for list_keys."""

    def test_returns_sorted_list(self):
        """list_keys returns a sorted list of strings."""
        config = CCDirectorConfig()
        keys = list_keys(config)
        assert isinstance(keys, list)
        assert keys == sorted(keys)
        assert len(keys) > 0

    def test_contains_expected_keys(self):
        """Known keys appear in the list."""
        config = CCDirectorConfig()
        keys = list_keys(config)
        assert "llm.default_provider" in keys
        assert "vault.vault_path" in keys
        assert "screenshots.source_directory" in keys
        assert "comm_manager.queue_path" in keys


class TestGetSectionNames:
    """Tests for get_section_names."""

    def test_returns_sorted_sections(self):
        """Section names are sorted."""
        config = CCDirectorConfig()
        names = get_section_names(config)
        assert names == sorted(names)

    def test_includes_all_sections(self):
        """All expected sections are present."""
        config = CCDirectorConfig()
        names = get_section_names(config)
        assert "llm" in names
        assert "photos" in names
        assert "vault" in names
        assert "comm_manager" in names
        assert "screenshots" in names
