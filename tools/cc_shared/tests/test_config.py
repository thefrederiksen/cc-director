"""Tests for cc_shared configuration module."""

import json
import os
import pytest
from pathlib import Path
from unittest.mock import patch

# Add parent directory to path so we can import cc_shared
import sys
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.config import (
    CCDirectorConfig,
    CommManagerConfig,
    LLMConfig,
    OpenAIProviderConfig,
    ClaudeCodeProviderConfig,
    PhotoSource,
    PhotosConfig,
    ScreenshotsConfig,
    SendFromAccount,
    VaultConfig,
    get_config,
    get_config_path,
    get_data_dir,
    get_install_dir,
    reload_config,
)


class TestGetDataDir:
    """Tests for data directory resolution via CcStorage."""

    def test_delegates_to_ccstorage_config(self, tmp_path):
        """get_data_dir returns CcStorage.config() path."""
        with patch.dict(os.environ, {"CC_DIRECTOR_ROOT": str(tmp_path)}):
            result = get_data_dir()
        assert result == tmp_path / "config"

    def test_localappdata_path(self, tmp_path):
        """Uses LOCALAPPDATA/cc-director/config when LOCALAPPDATA is set."""
        env = {k: v for k, v in os.environ.items() if k != "CC_DIRECTOR_ROOT"}
        env["LOCALAPPDATA"] = str(tmp_path)
        with patch.dict(os.environ, env, clear=True):
            result = get_data_dir()
        assert result == tmp_path / "cc-director" / "config"


class TestGetInstallDir:
    """Tests for install directory resolution via CcStorage."""

    def test_localappdata_path(self, tmp_path):
        """Install dir resolves to LOCALAPPDATA/cc-director/bin."""
        with patch.dict(os.environ, {"LOCALAPPDATA": str(tmp_path)}):
            result = get_install_dir()
        assert result == tmp_path / "cc-director" / "bin"

    def test_fallback_without_localappdata(self):
        """Falls back to ~/.cc-director/bin without LOCALAPPDATA."""
        env = {k: v for k, v in os.environ.items() if k != "LOCALAPPDATA"}
        with patch.dict(os.environ, env, clear=True):
            result = get_install_dir()
        assert result == Path.home() / ".cc-director" / "bin"


class TestCCDirectorConfig:
    """Tests for the main configuration class."""

    def test_default_values(self):
        """Config has sensible defaults without a config file."""
        config = CCDirectorConfig()
        assert config.llm.default_provider == "claude_code"
        assert config.llm.providers.openai.api_key_env == "OPENAI_API_KEY"
        assert config.llm.providers.openai.default_model == "gpt-4o-mini"
        assert config.llm.providers.openai.vision_model == "gpt-4o"
        assert config.llm.providers.claude_code.enabled is True

    def test_load_from_file(self, tmp_path):
        """Config loads correctly from a JSON file."""
        config_data = {
            "llm": {
                "default_provider": "openai",
                "providers": {
                    "openai": {
                        "api_key_env": "MY_CUSTOM_KEY",
                        "default_model": "gpt-5",
                        "vision_model": "gpt-5-vision",
                    },
                    "claude_code": {"enabled": False},
                },
            },
            "vault": {"vault_path": "/custom/vault"},
        }
        config_file = tmp_path / "config.json"
        config_file.write_text(json.dumps(config_data))

        config = CCDirectorConfig()
        config._config_path = config_file
        config.load()

        assert config.llm.default_provider == "openai"
        assert config.llm.providers.openai.api_key_env == "MY_CUSTOM_KEY"
        assert config.llm.providers.openai.default_model == "gpt-5"
        assert config.llm.providers.claude_code.enabled is False
        assert config.vault.vault_path == "/custom/vault"

    def test_load_missing_file_uses_defaults(self, tmp_path):
        """Missing config file should use defaults, not error."""
        config = CCDirectorConfig()
        config._config_path = tmp_path / "nonexistent.json"
        config.load()

        assert config.llm.default_provider == "claude_code"

    def test_load_corrupted_file_uses_defaults(self, tmp_path):
        """Corrupted config file should use defaults, not crash."""
        config_file = tmp_path / "config.json"
        config_file.write_text("not valid json {{{")

        config = CCDirectorConfig()
        config._config_path = config_file
        config.load()

        assert config.llm.default_provider == "claude_code"

    def test_save_and_reload(self, tmp_path):
        """Config should round-trip through save and load."""
        config_file = tmp_path / "config.json"

        # Create and save
        config1 = CCDirectorConfig()
        config1._config_path = config_file
        config1.llm.default_provider = "openai"
        config1.vault.vault_path = "/test/vault"
        config1.save()

        # Reload
        config2 = CCDirectorConfig()
        config2._config_path = config_file
        config2.load()

        assert config2.llm.default_provider == "openai"
        assert config2.vault.vault_path == "/test/vault"

    def test_to_dict_structure(self):
        """to_dict produces the expected JSON structure."""
        config = CCDirectorConfig()
        d = config.to_dict()

        assert "llm" in d
        assert "photos" in d
        assert "vault" in d
        assert "comm_manager" in d
        assert "screenshots" in d
        assert "providers" in d["llm"]
        assert "openai" in d["llm"]["providers"]
        assert "claude_code" in d["llm"]["providers"]

    def test_partial_config_file(self, tmp_path):
        """Config file with only some sections should merge with defaults."""
        config_data = {"llm": {"default_provider": "openai"}}
        config_file = tmp_path / "config.json"
        config_file.write_text(json.dumps(config_data))

        config = CCDirectorConfig()
        config._config_path = config_file
        config.load()

        # Specified value loaded
        assert config.llm.default_provider == "openai"
        # Unspecified sections use defaults
        assert config.llm.providers.openai.default_model == "gpt-4o-mini"


class TestPhotoSource:
    """Tests for PhotoSource dataclass."""

    def test_to_dict(self):
        """PhotoSource serializes to dict."""
        source = PhotoSource(
            path="D:/Photos", category="private", label="Family", priority=1
        )
        d = source.to_dict()
        assert d["path"] == "D:/Photos"
        assert d["category"] == "private"
        assert d["label"] == "Family"
        assert d["priority"] == 1

    def test_from_dict(self):
        """PhotoSource deserializes from dict."""
        data = {
            "path": "D:/Work",
            "category": "work",
            "label": "Office",
            "priority": 5,
        }
        source = PhotoSource.from_dict(data)
        assert source.path == "D:/Work"
        assert source.category == "work"
        assert source.label == "Office"
        assert source.priority == 5

    def test_from_dict_default_priority(self):
        """PhotoSource uses default priority when not specified."""
        data = {"path": "/tmp", "category": "other", "label": "Temp"}
        source = PhotoSource.from_dict(data)
        assert source.priority == 10

    def test_round_trip(self):
        """PhotoSource round-trips through dict serialization."""
        original = PhotoSource(
            path="/photos", category="private", label="Mine", priority=3
        )
        restored = PhotoSource.from_dict(original.to_dict())
        assert original.path == restored.path
        assert original.category == restored.category
        assert original.label == restored.label
        assert original.priority == restored.priority


class TestPhotoSourceManagement:
    """Tests for photo source add/remove on CCDirectorConfig."""

    def test_add_photo_source(self):
        """Adding a photo source works."""
        config = CCDirectorConfig()
        source = config.add_photo_source("D:/Photos", "private", "Family", 1)
        assert source.label == "Family"
        assert len(config.photos.sources) == 1

    def test_add_replaces_same_label(self):
        """Adding a source with existing label replaces it."""
        config = CCDirectorConfig()
        config.add_photo_source("D:/Old", "private", "Family", 1)
        config.add_photo_source("D:/New", "private", "Family", 2)
        assert len(config.photos.sources) == 1
        assert config.photos.sources[0].path == "D:/New"

    def test_add_sorts_by_priority(self):
        """Sources are sorted by priority after add."""
        config = CCDirectorConfig()
        config.add_photo_source("D:/Low", "other", "Low", 10)
        config.add_photo_source("D:/High", "private", "High", 1)
        config.add_photo_source("D:/Mid", "work", "Mid", 5)
        labels = [s.label for s in config.photos.sources]
        assert labels == ["High", "Mid", "Low"]

    def test_remove_photo_source(self):
        """Removing a source by label works."""
        config = CCDirectorConfig()
        config.add_photo_source("D:/Photos", "private", "Family", 1)
        removed = config.remove_photo_source("Family")
        assert removed is True
        assert len(config.photos.sources) == 0

    def test_remove_nonexistent_returns_false(self):
        """Removing a nonexistent label returns False."""
        config = CCDirectorConfig()
        removed = config.remove_photo_source("DoesNotExist")
        assert removed is False

    def test_get_photo_source(self):
        """Getting a source by label works."""
        config = CCDirectorConfig()
        config.add_photo_source("D:/Photos", "private", "Family", 1)
        source = config.get_photo_source("Family")
        assert source is not None
        assert source.path == "D:/Photos"

    def test_get_nonexistent_returns_none(self):
        """Getting a nonexistent label returns None."""
        config = CCDirectorConfig()
        assert config.get_photo_source("Nope") is None


class TestSendFromAccount:
    """Tests for SendFromAccount dataclass."""

    def test_to_dict(self):
        """SendFromAccount serializes to dict."""
        acct = SendFromAccount(
            email="user@example.com", tool="cc-gmail", tool_account="personal"
        )
        d = acct.to_dict()
        assert d["email"] == "user@example.com"
        assert d["tool"] == "cc-gmail"
        assert d["tool_account"] == "personal"

    def test_from_dict(self):
        """SendFromAccount deserializes from dict."""
        data = {"email": "cto@company.com", "tool": "cc-outlook", "tool_account": None}
        acct = SendFromAccount.from_dict(data)
        assert acct.email == "cto@company.com"
        assert acct.tool == "cc-outlook"
        assert acct.tool_account is None

    def test_round_trip(self):
        """SendFromAccount round-trips through dict."""
        original = SendFromAccount(
            email="test@test.com", tool="cc-gmail", tool_account="work"
        )
        restored = SendFromAccount.from_dict(original.to_dict())
        assert original.email == restored.email
        assert original.tool == restored.tool
        assert original.tool_account == restored.tool_account

    def test_default_tool_account_none(self):
        """SendFromAccount defaults tool_account to None."""
        data = {"email": "a@b.com", "tool": "cc-outlook"}
        acct = SendFromAccount.from_dict(data)
        assert acct.tool_account is None


class TestCommManagerConfig:
    """Tests for CommManagerConfig."""

    def test_defaults(self):
        """CommManagerConfig has expected defaults."""
        cfg = CommManagerConfig()
        assert cfg.default_persona == "personal"
        assert cfg.default_created_by == "claude_code"
        assert cfg.send_from_accounts == {}

    def test_path_methods(self):
        """Path methods return correct subdirectories."""
        cfg = CommManagerConfig(queue_path="D:/queue")
        assert cfg.get_queue_path() == Path("D:/queue")
        assert cfg.get_pending_path() == Path("D:/queue/pending_review")
        assert cfg.get_approved_path() == Path("D:/queue/approved")
        assert cfg.get_rejected_path() == Path("D:/queue/rejected")
        assert cfg.get_posted_path() == Path("D:/queue/posted")

    def test_round_trip(self):
        """CommManagerConfig round-trips through dict."""
        original = CommManagerConfig(
            queue_path="/custom", default_persona="mindzie", default_created_by="bot"
        )
        restored = CommManagerConfig.from_dict(original.to_dict())
        assert original.queue_path == restored.queue_path
        assert original.default_persona == restored.default_persona
        assert original.default_created_by == restored.default_created_by

    def test_round_trip_with_accounts(self):
        """CommManagerConfig with send_from_accounts round-trips through dict."""
        accounts = {
            "mindzie": SendFromAccount("cto@company.com", "cc-outlook", None),
            "personal": SendFromAccount("me@home.com", "cc-gmail", "personal"),
        }
        original = CommManagerConfig(
            queue_path="/custom",
            send_from_accounts=accounts,
        )
        d = original.to_dict()
        assert "send_from_accounts" in d
        assert d["send_from_accounts"]["mindzie"]["email"] == "cto@company.com"

        restored = CommManagerConfig.from_dict(d)
        assert len(restored.send_from_accounts) == 2
        assert restored.send_from_accounts["mindzie"].email == "cto@company.com"
        assert restored.send_from_accounts["personal"].tool_account == "personal"

    def test_get_valid_account_names(self):
        """get_valid_account_names returns account keys."""
        accounts = {
            "mindzie": SendFromAccount("a@b.com", "cc-outlook", None),
            "personal": SendFromAccount("c@d.com", "cc-gmail", "personal"),
        }
        cfg = CommManagerConfig(send_from_accounts=accounts)
        names = cfg.get_valid_account_names()
        assert "mindzie" in names
        assert "personal" in names

    def test_get_account_email(self):
        """get_account_email returns email for known account."""
        accounts = {
            "personal": SendFromAccount("me@home.com", "cc-gmail", "personal"),
        }
        cfg = CommManagerConfig(send_from_accounts=accounts)
        assert cfg.get_account_email("personal") == "me@home.com"
        assert cfg.get_account_email("bogus") is None

    def test_empty_accounts_default(self):
        """Empty accounts work without errors."""
        cfg = CommManagerConfig()
        assert cfg.get_valid_account_names() == []
        assert cfg.get_account_email("anything") is None
        assert cfg.get_account("anything") is None


class TestVaultConfig:
    """Tests for VaultConfig."""

    def test_round_trip(self):
        """VaultConfig round-trips through dict."""
        original = VaultConfig(vault_path="/my/vault")
        restored = VaultConfig.from_dict(original.to_dict())
        assert original.vault_path == restored.vault_path


class TestPhotosConfig:
    """Tests for PhotosConfig."""

    def test_default_database_path_uses_ccstorage(self):
        """Default database path resolves through CcStorage, not hardcoded ~/.cc-tools."""
        cfg = PhotosConfig()
        assert "photos.db" in cfg.database_path
        assert ".cc-tools" not in cfg.database_path
        assert "config/photos" in cfg.database_path.replace("\\", "/")

    def test_database_path_expansion(self):
        """Database path with ~ expands to home directory."""
        cfg = PhotosConfig(database_path="~/custom/photos.db")
        expanded = cfg.get_database_path()
        assert "~" not in str(expanded)
        assert str(expanded).endswith("photos.db")

    def test_round_trip_with_sources(self):
        """PhotosConfig with sources round-trips through dict."""
        original = PhotosConfig(
            database_path="/db/photos.db",
            sources=[
                PhotoSource("D:/A", "private", "A", 1),
                PhotoSource("D:/B", "work", "B", 2),
            ],
        )
        restored = PhotosConfig.from_dict(original.to_dict())
        assert restored.database_path == "/db/photos.db"
        assert len(restored.sources) == 2
        assert restored.sources[0].label == "A"
        assert restored.sources[1].label == "B"


class TestScreenshotsConfig:
    """Tests for ScreenshotsConfig."""

    def test_defaults_not_empty(self):
        """ScreenshotsConfig auto-detects a default path on Windows."""
        cfg = ScreenshotsConfig()
        # On a Windows machine with USERPROFILE set, should find something
        if os.environ.get("USERPROFILE"):
            assert cfg.source_directory != ""

    def test_explicit_path(self):
        """ScreenshotsConfig accepts an explicit path."""
        cfg = ScreenshotsConfig(source_directory="D:/My/Screenshots")
        assert cfg.source_directory == "D:/My/Screenshots"

    def test_get_source_directory(self):
        """get_source_directory returns a Path object."""
        cfg = ScreenshotsConfig(source_directory="D:/Screenshots")
        assert cfg.get_source_directory() == Path("D:/Screenshots")

    def test_to_dict(self):
        """ScreenshotsConfig serializes to dict."""
        cfg = ScreenshotsConfig(source_directory="D:/Pics/Screenshots")
        d = cfg.to_dict()
        assert d["source_directory"] == "D:/Pics/Screenshots"

    def test_from_dict(self):
        """ScreenshotsConfig deserializes from dict."""
        data = {"source_directory": "/custom/screenshots"}
        cfg = ScreenshotsConfig.from_dict(data)
        assert cfg.source_directory == "/custom/screenshots"

    def test_round_trip(self):
        """ScreenshotsConfig round-trips through dict."""
        original = ScreenshotsConfig(source_directory="D:/Test/Screenshots")
        restored = ScreenshotsConfig.from_dict(original.to_dict())
        assert original.source_directory == restored.source_directory

    def test_save_and_reload_with_screenshots(self, tmp_path):
        """Screenshots config persists through save/load cycle."""
        config_file = tmp_path / "config.json"

        config1 = CCDirectorConfig()
        config1._config_path = config_file
        config1.screenshots.source_directory = "D:/Custom/Screenshots"
        config1.save()

        config2 = CCDirectorConfig()
        config2._config_path = config_file
        config2.load()

        assert config2.screenshots.source_directory == "D:/Custom/Screenshots"

    def test_partial_config_preserves_screenshots_default(self, tmp_path):
        """Config without screenshots section uses defaults."""
        config_data = {"llm": {"default_provider": "openai"}}
        config_file = tmp_path / "config.json"
        config_file.write_text(json.dumps(config_data))

        config = CCDirectorConfig()
        config._config_path = config_file
        config.load()

        # Screenshots should have a default, not be empty
        assert isinstance(config.screenshots, ScreenshotsConfig)

    def test_save_and_reload_with_send_from_accounts(self, tmp_path):
        """Send-from accounts persist through save/load cycle."""
        config_file = tmp_path / "config.json"

        config1 = CCDirectorConfig()
        config1._config_path = config_file
        config1.comm_manager.send_from_accounts = {
            "work": SendFromAccount("work@co.com", "cc-outlook", None),
            "personal": SendFromAccount("me@home.com", "cc-gmail", "personal"),
        }
        config1.save()

        config2 = CCDirectorConfig()
        config2._config_path = config_file
        config2.load()

        assert len(config2.comm_manager.send_from_accounts) == 2
        assert config2.comm_manager.get_account_email("work") == "work@co.com"
        assert config2.comm_manager.send_from_accounts["personal"].tool == "cc-gmail"
