"""Configuration loading for cc_director."""

import os
from dataclasses import dataclass
from pathlib import Path
from typing import Optional


@dataclass
class Config:
    """cc_director configuration settings."""
    db_path: str
    log_dir: str
    log_level: str
    check_interval: int
    shutdown_timeout: int
    # Gateway settings
    gateway_enabled: bool
    gateway_host: str
    gateway_port: int

    @classmethod
    def load(cls, config_file: Optional[str] = None) -> "Config":
        """
        Load configuration from environment variables and optional config file.

        Environment variables take precedence over config file.

        Args:
            config_file: Optional path to config file

        Returns:
            Config instance with loaded values
        """
        # Defaults
        defaults = {
            "db_path": "./cc_director.db",
            "log_dir": "./logs",
            "log_level": "INFO",
            "check_interval": 60,
            "shutdown_timeout": 30,
            "gateway_enabled": False,
            "gateway_host": "0.0.0.0",
            "gateway_port": 6060,
        }

        # Load from config file if provided
        file_config = {}
        if config_file and Path(config_file).exists():
            file_config = cls._parse_config_file(config_file)

        # Environment variables override file config
        db_path = os.environ.get(
            "CC_DIRECTOR_DB",
            file_config.get("db_path", defaults["db_path"])
        )
        log_dir = os.environ.get(
            "CC_DIRECTOR_LOG_DIR",
            file_config.get("log_dir", defaults["log_dir"])
        )
        log_level = os.environ.get(
            "CC_DIRECTOR_LOG_LEVEL",
            file_config.get("log_level", defaults["log_level"])
        )
        check_interval = int(os.environ.get(
            "CC_DIRECTOR_CHECK_INTERVAL",
            file_config.get("check_interval", defaults["check_interval"])
        ))
        shutdown_timeout = int(os.environ.get(
            "CC_DIRECTOR_SHUTDOWN_TIMEOUT",
            file_config.get("shutdown_timeout", defaults["shutdown_timeout"])
        ))
        gateway_enabled = os.environ.get(
            "CC_DIRECTOR_GATEWAY_ENABLED",
            file_config.get("gateway_enabled", defaults["gateway_enabled"])
        )
        # Handle string "true"/"false" from env vars
        if isinstance(gateway_enabled, str):
            gateway_enabled = gateway_enabled.lower() in ("true", "1", "yes")
        gateway_host = os.environ.get(
            "CC_DIRECTOR_GATEWAY_HOST",
            file_config.get("gateway_host", defaults["gateway_host"])
        )
        gateway_port = int(os.environ.get(
            "CC_DIRECTOR_GATEWAY_PORT",
            file_config.get("gateway_port", defaults["gateway_port"])
        ))

        return cls(
            db_path=db_path,
            log_dir=log_dir,
            log_level=log_level,
            check_interval=check_interval,
            shutdown_timeout=shutdown_timeout,
            gateway_enabled=gateway_enabled,
            gateway_host=gateway_host,
            gateway_port=gateway_port,
        )

    @staticmethod
    def _parse_config_file(path: str) -> dict:
        """
        Parse a simple key=value config file.

        Args:
            path: Path to config file

        Returns:
            Dictionary of config values
        """
        config = {}
        with open(path, "r") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                if "=" in line:
                    key, value = line.split("=", 1)
                    config[key.strip().lower()] = value.strip()
        return config

    def get_db_path(self) -> Path:
        """Get absolute path to database file."""
        return Path(self.db_path).resolve()

    def get_log_dir(self) -> Path:
        """Get absolute path to log directory."""
        path = Path(self.log_dir).resolve()
        path.mkdir(parents=True, exist_ok=True)
        return path
