"""Shared configuration and LLM abstraction for cc-director."""

__version__ = "0.1.0"

from .config import CCDirectorConfig, get_config, get_config_path
from .llm import LLMProvider, get_llm_provider

__all__ = [
    "CCDirectorConfig",
    "get_config",
    "get_config_path",
    "LLMProvider",
    "get_llm_provider",
]
