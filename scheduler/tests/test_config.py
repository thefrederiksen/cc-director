"""Tests for config module."""

import os
import pytest
import tempfile
from pathlib import Path

from cc_director.config import Config


class TestConfigLoad:
    """Tests for configuration loading."""

    def test_default_values(self):
        """Should use default values when nothing is set."""
        # Clear any environment variables
        env_vars = [
            "CC_DIRECTOR_DB",
            "CC_DIRECTOR_LOG_DIR",
            "CC_DIRECTOR_LOG_LEVEL",
            "CC_DIRECTOR_CHECK_INTERVAL",
            "CC_DIRECTOR_SHUTDOWN_TIMEOUT",
        ]
        original = {k: os.environ.get(k) for k in env_vars}

        for var in env_vars:
            if var in os.environ:
                del os.environ[var]

        try:
            config = Config.load()

            assert config.db_path == "./cc_director.db"
            assert config.log_dir == "./logs"
            assert config.log_level == "INFO"
            assert config.check_interval == 60
            assert config.shutdown_timeout == 30
        finally:
            # Restore environment
            for k, v in original.items():
                if v is not None:
                    os.environ[k] = v

    def test_environment_variables(self):
        """Should load from environment variables."""
        os.environ["CC_DIRECTOR_DB"] = "/custom/path.db"
        os.environ["CC_DIRECTOR_LOG_LEVEL"] = "DEBUG"
        os.environ["CC_DIRECTOR_CHECK_INTERVAL"] = "30"

        try:
            config = Config.load()

            assert config.db_path == "/custom/path.db"
            assert config.log_level == "DEBUG"
            assert config.check_interval == 30
        finally:
            del os.environ["CC_DIRECTOR_DB"]
            del os.environ["CC_DIRECTOR_LOG_LEVEL"]
            del os.environ["CC_DIRECTOR_CHECK_INTERVAL"]

    def test_config_file(self):
        """Should load from config file."""
        with tempfile.NamedTemporaryFile(mode="w", suffix=".conf", delete=False) as f:
            f.write("db_path = /from/file.db\n")
            f.write("log_level = WARNING\n")
            f.write("check_interval = 120\n")
            config_path = f.name

        try:
            # Clear environment
            for var in ["CC_DIRECTOR_DB", "CC_DIRECTOR_LOG_LEVEL", "CC_DIRECTOR_CHECK_INTERVAL"]:
                if var in os.environ:
                    del os.environ[var]

            config = Config.load(config_path)

            assert config.db_path == "/from/file.db"
            assert config.log_level == "WARNING"
            assert config.check_interval == 120
        finally:
            os.unlink(config_path)

    def test_env_overrides_file(self):
        """Environment variables should override config file."""
        with tempfile.NamedTemporaryFile(mode="w", suffix=".conf", delete=False) as f:
            f.write("db_path = /from/file.db\n")
            f.write("log_level = WARNING\n")
            config_path = f.name

        os.environ["CC_DIRECTOR_LOG_LEVEL"] = "ERROR"

        try:
            config = Config.load(config_path)

            assert config.db_path == "/from/file.db"  # From file
            assert config.log_level == "ERROR"  # From env (override)
        finally:
            os.unlink(config_path)
            del os.environ["CC_DIRECTOR_LOG_LEVEL"]


class TestConfigPaths:
    """Tests for path resolution."""

    def test_get_db_path_absolute(self):
        """Should resolve relative db_path to absolute."""
        config = Config(
            db_path="./test.db",
            log_dir="./logs",
            log_level="INFO",
            check_interval=60,
            shutdown_timeout=30,
        )

        path = config.get_db_path()
        assert path.is_absolute()
        assert path.name == "test.db"

    def test_get_log_dir_creates(self, tmp_path):
        """Should create log directory if it doesn't exist."""
        log_dir = tmp_path / "new_logs"
        config = Config(
            db_path="./test.db",
            log_dir=str(log_dir),
            log_level="INFO",
            check_interval=60,
            shutdown_timeout=30,
        )

        path = config.get_log_dir()
        assert path.exists()
        assert path.is_dir()
