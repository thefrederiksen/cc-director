"""Shared configuration for cc-director.

Configuration is stored in the cc-director config directory.
All path resolution is delegated to cc_storage.CcStorage.
"""

import json
import logging
import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional

logger = logging.getLogger(__name__)

# Import cc_storage for centralized path resolution
try:
    from cc_storage import CcStorage
except ImportError:
    # Allow standalone usage when cc_storage is not installed
    import sys
    _tools_dir = str(Path(__file__).resolve().parent.parent)
    if _tools_dir not in sys.path:
        sys.path.insert(0, _tools_dir)
    from cc_storage import CcStorage


def get_data_dir() -> Path:
    """Get the cc-director config directory.

    Delegates to CcStorage.config() for centralized path resolution.
    Legacy callers that used this for tool data storage will now get
    the unified config directory.

    Returns:
        Path to the config directory
    """
    return CcStorage.config()


def get_install_dir() -> Path:
    """Get the cc-director installation directory (where executables live).

    Returns:
        Path to the bin directory containing cc-director executables.
    """
    return CcStorage.bin()


def get_config_path() -> Path:
    """Get the path to the cc-director config file."""
    return CcStorage.config_json()


def ensure_config_dir() -> Path:
    """Ensure the config directory exists and return the config path."""
    CcStorage.ensure(CcStorage.config())
    return get_config_path()


@dataclass
class OpenAIProviderConfig:
    """OpenAI provider configuration."""
    api_key_env: str = "OPENAI_API_KEY"
    default_model: str = "gpt-4o-mini"
    vision_model: str = "gpt-4o"


@dataclass
class ClaudeCodeProviderConfig:
    """Claude Code provider configuration."""
    enabled: bool = True


@dataclass
class LLMProvidersConfig:
    """LLM providers configuration."""
    openai: OpenAIProviderConfig = field(default_factory=OpenAIProviderConfig)
    claude_code: ClaudeCodeProviderConfig = field(default_factory=ClaudeCodeProviderConfig)


@dataclass
class LLMConfig:
    """LLM configuration."""
    default_provider: str = "claude_code"
    providers: LLMProvidersConfig = field(default_factory=LLMProvidersConfig)


@dataclass
class PhotoSource:
    """A photo source directory."""
    path: str
    category: str  # 'private', 'work', 'other'
    label: str
    priority: int = 10  # Lower = higher priority

    def to_dict(self) -> Dict[str, Any]:
        return {
            "path": self.path,
            "category": self.category,
            "label": self.label,
            "priority": self.priority,
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "PhotoSource":
        return cls(
            path=data["path"],
            category=data["category"],
            label=data["label"],
            priority=data.get("priority", 10),
        )


def _default_vault_path() -> str:
    """Compute the default vault path.

    Delegates to CcStorage.vault() for centralized path resolution.
    """
    return str(CcStorage.vault()).replace("\\", "/")


@dataclass
class VaultConfig:
    """Vault configuration."""
    vault_path: str = ""

    def __post_init__(self):
        if not self.vault_path:
            self.vault_path = _default_vault_path()

    def to_dict(self) -> Dict[str, Any]:
        return {"vault_path": self.vault_path}

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "VaultConfig":
        return cls(vault_path=data.get("vault_path", _default_vault_path()))


def _default_comm_manager_path() -> str:
    """Compute the default Communication Manager content path.

    Delegates to CcStorage.tool_config("comm-queue") for centralized path resolution.
    """
    return str(CcStorage.tool_config("comm-queue")).replace("\\", "/")


@dataclass
class CommManagerConfig:
    """Communication Manager configuration."""
    queue_path: str = ""

    def __post_init__(self):
        if not self.queue_path:
            self.queue_path = _default_comm_manager_path()
    default_persona: str = "personal"
    default_created_by: str = "claude_code"

    def get_queue_path(self) -> Path:
        """Get the queue path as a Path object."""
        return Path(self.queue_path)

    def get_pending_path(self) -> Path:
        """Get the pending_review directory path."""
        return self.get_queue_path() / "pending_review"

    def get_approved_path(self) -> Path:
        """Get the approved directory path."""
        return self.get_queue_path() / "approved"

    def get_rejected_path(self) -> Path:
        """Get the rejected directory path."""
        return self.get_queue_path() / "rejected"

    def get_posted_path(self) -> Path:
        """Get the posted directory path."""
        return self.get_queue_path() / "posted"

    def to_dict(self) -> Dict[str, Any]:
        return {
            "queue_path": self.queue_path,
            "default_persona": self.default_persona,
            "default_created_by": self.default_created_by,
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "CommManagerConfig":
        return cls(
            queue_path=data.get("queue_path", _default_comm_manager_path()),
            default_persona=data.get("default_persona", "personal"),
            default_created_by=data.get("default_created_by", "claude_code"),
        )


def _default_photos_db_path() -> str:
    """Compute the default photos database path.

    Delegates to CcStorage.tool_config("photos") for centralized path resolution.
    """
    return str(CcStorage.tool_config("photos") / "photos.db").replace("\\", "/")


@dataclass
class PhotosConfig:
    """Photos tool configuration."""
    database_path: str = ""
    sources: List[PhotoSource] = field(default_factory=list)

    def __post_init__(self):
        if not self.database_path:
            self.database_path = _default_photos_db_path()

    def get_database_path(self) -> Path:
        """Get the expanded database path."""
        return Path(os.path.expanduser(self.database_path))

    def to_dict(self) -> Dict[str, Any]:
        return {
            "database_path": self.database_path,
            "sources": [s.to_dict() for s in self.sources],
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "PhotosConfig":
        sources = [PhotoSource.from_dict(s) for s in data.get("sources", [])]
        return cls(
            database_path=data.get("database_path", _default_photos_db_path()),
            sources=sources,
        )


class CCDirectorConfig:
    """Main configuration class for cc-director."""

    def __init__(self):
        self.llm = LLMConfig()
        self.photos = PhotosConfig()
        self.vault = VaultConfig()
        self.comm_manager = CommManagerConfig()
        self._config_path = get_config_path()

    def load(self) -> "CCDirectorConfig":
        """Load configuration from file."""
        if self._config_path.exists():
            try:
                with open(self._config_path, "r", encoding="utf-8") as f:
                    data = json.load(f)
                self._load_from_dict(data)
            except (json.JSONDecodeError, KeyError, TypeError) as e:
                # Config file corrupted, use defaults
                logger.warning("Config file corrupted, using defaults: %s", e)
        return self

    def _load_from_dict(self, data: Dict[str, Any]) -> None:
        """Load configuration from dictionary."""
        # Load LLM config
        if "llm" in data:
            llm_data = data["llm"]
            self.llm.default_provider = llm_data.get("default_provider", "claude_code")

            if "providers" in llm_data:
                providers = llm_data["providers"]
                if "openai" in providers:
                    openai = providers["openai"]
                    self.llm.providers.openai = OpenAIProviderConfig(
                        api_key_env=openai.get("api_key_env", "OPENAI_API_KEY"),
                        default_model=openai.get("default_model", "gpt-4o-mini"),
                        vision_model=openai.get("vision_model", "gpt-4o"),
                    )
                if "claude_code" in providers:
                    claude = providers["claude_code"]
                    self.llm.providers.claude_code = ClaudeCodeProviderConfig(
                        enabled=claude.get("enabled", True),
                    )

        # Load photos config
        if "photos" in data:
            self.photos = PhotosConfig.from_dict(data["photos"])

        # Load vault config
        if "vault" in data:
            self.vault = VaultConfig.from_dict(data["vault"])

        # Load comm_manager config
        if "comm_manager" in data:
            self.comm_manager = CommManagerConfig.from_dict(data["comm_manager"])

    def save(self) -> None:
        """Save configuration to file."""
        ensure_config_dir()
        data = self.to_dict()
        with open(self._config_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)

    def to_dict(self) -> Dict[str, Any]:
        """Convert configuration to dictionary."""
        return {
            "llm": {
                "default_provider": self.llm.default_provider,
                "providers": {
                    "openai": {
                        "api_key_env": self.llm.providers.openai.api_key_env,
                        "default_model": self.llm.providers.openai.default_model,
                        "vision_model": self.llm.providers.openai.vision_model,
                    },
                    "claude_code": {
                        "enabled": self.llm.providers.claude_code.enabled,
                    },
                },
            },
            "photos": self.photos.to_dict(),
            "vault": self.vault.to_dict(),
            "comm_manager": self.comm_manager.to_dict(),
        }

    def add_photo_source(self, path: str, category: str, label: str, priority: int = 10) -> PhotoSource:
        """Add a photo source."""
        source = PhotoSource(path=path, category=category, label=label, priority=priority)
        # Remove existing source with same label
        self.photos.sources = [s for s in self.photos.sources if s.label != label]
        self.photos.sources.append(source)
        # Sort by priority
        self.photos.sources.sort(key=lambda s: s.priority)
        return source

    def remove_photo_source(self, label: str) -> bool:
        """Remove a photo source by label. Returns True if found and removed."""
        original_len = len(self.photos.sources)
        self.photos.sources = [s for s in self.photos.sources if s.label != label]
        return len(self.photos.sources) < original_len

    def get_photo_source(self, label: str) -> Optional[PhotoSource]:
        """Get a photo source by label."""
        for source in self.photos.sources:
            if source.label == label:
                return source
        return None


# Global config instance
_config: Optional[CCDirectorConfig] = None


def get_config() -> CCDirectorConfig:
    """Get the global configuration instance."""
    global _config
    if _config is None:
        _config = CCDirectorConfig().load()
    return _config


def reload_config() -> CCDirectorConfig:
    """Reload configuration from file."""
    global _config
    _config = CCDirectorConfig().load()
    return _config
