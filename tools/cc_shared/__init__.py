"""Shared configuration, LLM abstraction, and theme definitions for cc-director."""

__version__ = "0.1.0"

from .config import CCDirectorConfig, get_config, get_config_path
from .llm import LLMProvider, get_llm_provider
from .themes import (
    CanonicalTheme,
    ThemeColors,
    ThemeFonts,
    get_theme,
    list_themes,
)
from .markdown_parser import ParsedMarkdown, parse_markdown

__all__ = [
    "CCDirectorConfig",
    "get_config",
    "get_config_path",
    "LLMProvider",
    "get_llm_provider",
    "CanonicalTheme",
    "ThemeColors",
    "ThemeFonts",
    "get_theme",
    "list_themes",
    "ParsedMarkdown",
    "parse_markdown",
]
