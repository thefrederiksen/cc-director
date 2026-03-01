"""Shared configuration and LLM abstraction for cc-tools."""

__version__ = "0.1.0"

from .config import CCToolsConfig, get_config, get_config_path
from .llm import LLMProvider, get_llm_provider

__all__ = [
    "CCToolsConfig",
    "get_config",
    "get_config_path",
    "LLMProvider",
    "get_llm_provider",
]
