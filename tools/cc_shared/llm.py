"""LLM provider abstraction for cc-tools.

Supports OpenAI and Claude Code CLI as providers.
"""

import os
from abc import ABC, abstractmethod
from pathlib import Path
from typing import Optional

from .config import get_config


class LLMProvider(ABC):
    """Abstract base class for LLM providers."""

    @abstractmethod
    def describe_image(self, image_path: Path, prompt: Optional[str] = None) -> str:
        """Get a description of an image.

        Args:
            image_path: Path to the image file
            prompt: Optional custom prompt

        Returns:
            Description text
        """
        pass

    @abstractmethod
    def generate_text(self, prompt: str) -> str:
        """Generate text from a prompt.

        Args:
            prompt: The prompt

        Returns:
            Generated text
        """
        pass

    @abstractmethod
    def extract_text(self, image_path: Path, prompt: Optional[str] = None) -> str:
        """Extract text from an image (OCR).

        Args:
            image_path: Path to the image file
            prompt: Optional custom prompt

        Returns:
            Extracted text
        """
        pass

    @property
    @abstractmethod
    def name(self) -> str:
        """Provider name."""
        pass


class OpenAIProvider(LLMProvider):
    """OpenAI LLM provider using Vision API."""

    def __init__(self, api_key: Optional[str] = None, vision_model: Optional[str] = None):
        config = get_config()
        openai_config = config.llm.providers.openai

        self.api_key = api_key or os.environ.get(openai_config.api_key_env)
        if not self.api_key:
            raise ValueError(
                f"OpenAI API key not found. Set {openai_config.api_key_env} environment variable."
            )

        self.vision_model = vision_model or openai_config.vision_model
        self.text_model = openai_config.default_model

    @property
    def name(self) -> str:
        return "openai"

    def describe_image(self, image_path: Path, prompt: Optional[str] = None) -> str:
        """Describe an image using OpenAI Vision API."""
        import base64

        try:
            import openai
        except ImportError:
            raise ImportError("openai package not installed. Run: pip install openai")

        # Read and encode image
        with open(image_path, "rb") as f:
            image_data = base64.b64encode(f.read()).decode("utf-8")

        # Determine mime type
        suffix = image_path.suffix.lower()
        mime_types = {
            ".jpg": "image/jpeg",
            ".jpeg": "image/jpeg",
            ".png": "image/png",
            ".gif": "image/gif",
            ".webp": "image/webp",
        }
        mime_type = mime_types.get(suffix, "image/jpeg")

        default_prompt = (
            "Describe this image in detail. Include: main subjects, colors, "
            "setting/location, mood, any text visible, and notable details. "
            "Be concise but thorough."
        )

        client = openai.OpenAI(api_key=self.api_key)
        response = client.chat.completions.create(
            model=self.vision_model,
            messages=[
                {
                    "role": "user",
                    "content": [
                        {"type": "text", "text": prompt or default_prompt},
                        {
                            "type": "image_url",
                            "image_url": {
                                "url": f"data:{mime_type};base64,{image_data}",
                            },
                        },
                    ],
                }
            ],
            max_tokens=500,
        )

        return response.choices[0].message.content

    def generate_text(self, prompt: str) -> str:
        """Generate text using OpenAI."""
        try:
            import openai
        except ImportError:
            raise ImportError("openai package not installed. Run: pip install openai")

        client = openai.OpenAI(api_key=self.api_key)
        response = client.chat.completions.create(
            model=self.text_model,
            messages=[{"role": "user", "content": prompt}],
            max_tokens=500,
        )

        return response.choices[0].message.content

    def extract_text(self, image_path: Path, prompt: Optional[str] = None) -> str:
        """Extract text from image using OpenAI Vision API (OCR)."""
        import base64

        try:
            import openai
        except ImportError:
            raise ImportError("openai package not installed. Run: pip install openai")

        # Read and encode image
        with open(image_path, "rb") as f:
            image_data = base64.b64encode(f.read()).decode("utf-8")

        # Determine mime type
        suffix = image_path.suffix.lower()
        mime_types = {
            ".jpg": "image/jpeg",
            ".jpeg": "image/jpeg",
            ".png": "image/png",
            ".gif": "image/gif",
            ".webp": "image/webp",
        }
        mime_type = mime_types.get(suffix, "image/jpeg")

        default_prompt = (
            "Extract and return all text visible in this image. "
            "Return only the text, nothing else."
        )

        client = openai.OpenAI(api_key=self.api_key)
        response = client.chat.completions.create(
            model=self.vision_model,
            messages=[
                {
                    "role": "user",
                    "content": [
                        {"type": "text", "text": prompt or default_prompt},
                        {
                            "type": "image_url",
                            "image_url": {
                                "url": f"data:{mime_type};base64,{image_data}",
                            },
                        },
                    ],
                }
            ],
            max_tokens=1000,
        )

        return response.choices[0].message.content


class ClaudeCodeProvider(LLMProvider):
    """Claude Code CLI provider.

    Uses the Claude Code CLI to run vision and text tasks.
    This requires Claude Code to be installed and authenticated.
    """

    def __init__(self):
        config = get_config()
        if not config.llm.providers.claude_code.enabled:
            raise ValueError("Claude Code provider is disabled in configuration.")

    @property
    def name(self) -> str:
        return "claude_code"

    def describe_image(self, image_path: Path, prompt: Optional[str] = None) -> str:
        """Describe an image using Claude Code CLI."""
        import subprocess

        default_prompt = (
            "Describe this image in detail. Include: main subjects, colors, "
            "setting/location, mood, any text visible, and notable details. "
            "Be concise but thorough. Output ONLY the description, no preamble."
        )

        full_prompt = f"Read and describe this image: {image_path}. {prompt or default_prompt}"

        # Use claude command with the image
        try:
            result = subprocess.run(
                [
                    "claude",
                    "--print",
                    "--dangerously-skip-permissions",
                    full_prompt,
                ],
                capture_output=True,
                text=True,
                timeout=120,
            )

            if result.returncode != 0:
                raise RuntimeError(f"Claude Code failed: {result.stderr}")

            return result.stdout.strip()

        except FileNotFoundError:
            raise RuntimeError(
                "Claude Code CLI not found. Install with: npm install -g @anthropic-ai/claude-code"
            )
        except subprocess.TimeoutExpired:
            raise RuntimeError("Claude Code timed out")

    def generate_text(self, prompt: str) -> str:
        """Generate text using Claude Code CLI."""
        import subprocess

        try:
            result = subprocess.run(
                [
                    "claude",
                    "--print",
                    "--dangerously-skip-permissions",
                    prompt,
                ],
                capture_output=True,
                text=True,
                timeout=120,
            )

            if result.returncode != 0:
                raise RuntimeError(f"Claude Code failed: {result.stderr}")

            return result.stdout.strip()

        except FileNotFoundError:
            raise RuntimeError(
                "Claude Code CLI not found. Install with: npm install -g @anthropic-ai/claude-code"
            )
        except subprocess.TimeoutExpired:
            raise RuntimeError("Claude Code timed out")

    def extract_text(self, image_path: Path, prompt: Optional[str] = None) -> str:
        """Extract text from image using Claude Code CLI (OCR)."""
        import subprocess

        default_prompt = (
            "Extract and return all text visible in this image. "
            "Return only the text, nothing else. No preamble, no explanation."
        )

        full_prompt = f"Read this image: {image_path}. {prompt or default_prompt}"

        try:
            result = subprocess.run(
                [
                    "claude",
                    "--print",
                    "--dangerously-skip-permissions",
                    full_prompt,
                ],
                capture_output=True,
                text=True,
                timeout=120,
            )

            if result.returncode != 0:
                raise RuntimeError(f"Claude Code failed: {result.stderr}")

            return result.stdout.strip()

        except FileNotFoundError:
            raise RuntimeError(
                "Claude Code CLI not found. Install with: npm install -g @anthropic-ai/claude-code"
            )
        except subprocess.TimeoutExpired:
            raise RuntimeError("Claude Code timed out")


def get_llm_provider(provider_name: Optional[str] = None) -> LLMProvider:
    """Get an LLM provider instance.

    Args:
        provider_name: Provider name ('openai' or 'claude_code').
                      If None, uses default from config.

    Returns:
        LLMProvider instance
    """
    config = get_config()
    name = provider_name or config.llm.default_provider

    if name == "openai":
        return OpenAIProvider()
    elif name == "claude_code":
        return ClaudeCodeProvider()
    else:
        raise ValueError(f"Unknown provider: {name}. Valid options: openai, claude_code")
