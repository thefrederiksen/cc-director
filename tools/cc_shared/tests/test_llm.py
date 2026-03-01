"""Tests for cc_shared LLM provider module."""

import os
import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.llm import (
    LLMProvider,
    OpenAIProvider,
    ClaudeCodeProvider,
    get_llm_provider,
)
from cc_shared.config import CCToolsConfig


class TestLLMProviderInterface:
    """Tests for the abstract LLMProvider interface."""

    def test_cannot_instantiate_abstract(self):
        """LLMProvider is abstract and cannot be instantiated."""
        with pytest.raises(TypeError):
            LLMProvider()

    def test_interface_methods_exist(self):
        """LLMProvider defines the required interface methods."""
        assert hasattr(LLMProvider, "describe_image")
        assert hasattr(LLMProvider, "generate_text")
        assert hasattr(LLMProvider, "extract_text")
        assert hasattr(LLMProvider, "name")


class TestOpenAIProvider:
    """Tests for the OpenAI provider."""

    def test_requires_api_key(self):
        """OpenAIProvider raises ValueError without API key."""
        env = {k: v for k, v in os.environ.items() if k != "OPENAI_API_KEY"}
        with patch.dict(os.environ, env, clear=True):
            with patch("cc_shared.llm.get_config") as mock_config:
                config = CCToolsConfig()
                mock_config.return_value = config
                with pytest.raises(ValueError, match="API key not found"):
                    OpenAIProvider()

    def test_accepts_explicit_api_key(self):
        """OpenAIProvider accepts an explicit API key."""
        with patch("cc_shared.llm.get_config") as mock_config:
            config = CCToolsConfig()
            mock_config.return_value = config
            provider = OpenAIProvider(api_key="test-key-123")
        assert provider.api_key == "test-key-123"
        assert provider.name == "openai"

    def test_reads_api_key_from_env(self):
        """OpenAIProvider reads API key from environment."""
        with patch.dict(os.environ, {"OPENAI_API_KEY": "env-key-456"}):
            with patch("cc_shared.llm.get_config") as mock_config:
                config = CCToolsConfig()
                mock_config.return_value = config
                provider = OpenAIProvider()
        assert provider.api_key == "env-key-456"

    def test_uses_config_model_names(self):
        """OpenAIProvider uses model names from config."""
        with patch("cc_shared.llm.get_config") as mock_config:
            config = CCToolsConfig()
            config.llm.providers.openai.vision_model = "gpt-5-vision"
            config.llm.providers.openai.default_model = "gpt-5"
            mock_config.return_value = config
            provider = OpenAIProvider(api_key="test")
        assert provider.vision_model == "gpt-5-vision"
        assert provider.text_model == "gpt-5"

    def test_explicit_model_overrides_config(self):
        """Explicit vision_model parameter overrides config."""
        with patch("cc_shared.llm.get_config") as mock_config:
            config = CCToolsConfig()
            mock_config.return_value = config
            provider = OpenAIProvider(api_key="test", vision_model="custom-model")
        assert provider.vision_model == "custom-model"


class TestClaudeCodeProvider:
    """Tests for the Claude Code CLI provider."""

    def test_name_is_claude_code(self):
        """ClaudeCodeProvider has correct name."""
        with patch("cc_shared.llm.get_config") as mock_config:
            config = CCToolsConfig()
            mock_config.return_value = config
            provider = ClaudeCodeProvider()
        assert provider.name == "claude_code"

    def test_raises_when_disabled(self):
        """ClaudeCodeProvider raises when disabled in config."""
        with patch("cc_shared.llm.get_config") as mock_config:
            config = CCToolsConfig()
            config.llm.providers.claude_code.enabled = False
            mock_config.return_value = config
            with pytest.raises(ValueError, match="disabled"):
                ClaudeCodeProvider()


class TestGetLLMProvider:
    """Tests for the provider factory function."""

    def test_returns_openai_when_requested(self):
        """get_llm_provider('openai') returns OpenAIProvider."""
        with patch("cc_shared.llm.get_config") as mock_config:
            config = CCToolsConfig()
            mock_config.return_value = config
            with patch.dict(os.environ, {"OPENAI_API_KEY": "test-key"}):
                provider = get_llm_provider("openai")
        assert isinstance(provider, OpenAIProvider)

    def test_returns_claude_code_when_requested(self):
        """get_llm_provider('claude_code') returns ClaudeCodeProvider."""
        with patch("cc_shared.llm.get_config") as mock_config:
            config = CCToolsConfig()
            mock_config.return_value = config
            provider = get_llm_provider("claude_code")
        assert isinstance(provider, ClaudeCodeProvider)

    def test_uses_config_default_when_no_name(self):
        """get_llm_provider() uses default_provider from config."""
        with patch("cc_shared.llm.get_config") as mock_config:
            config = CCToolsConfig()
            config.llm.default_provider = "claude_code"
            mock_config.return_value = config
            provider = get_llm_provider()
        assert isinstance(provider, ClaudeCodeProvider)

    def test_unknown_provider_raises(self):
        """Unknown provider name raises ValueError."""
        with patch("cc_shared.llm.get_config") as mock_config:
            config = CCToolsConfig()
            mock_config.return_value = config
            with pytest.raises(ValueError, match="Unknown provider"):
                get_llm_provider("nonexistent")
