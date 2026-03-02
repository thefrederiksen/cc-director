"""Configuration management for cc-posthog using CcStorage."""

import json
from pathlib import Path
from typing import Optional

from pydantic import BaseModel, Field

try:
    from cc_storage import CcStorage
except ImportError:
    import sys
    # cc_storage is a sibling package under tools/
    _tools_dir = str(Path(__file__).resolve().parent.parent.parent)
    if _tools_dir not in sys.path:
        sys.path.insert(0, _tools_dir)
    from cc_storage import CcStorage


class ProjectConfig(BaseModel):
    """Configuration for a single PostHog project."""
    project_id: int
    host: str = "https://us.posthog.com"
    funnel_steps: list[str] = Field(default_factory=list)


class PostHogConfig(BaseModel):
    """Root configuration for cc-posthog."""
    api_key: str = ""
    default_project: str = ""
    projects: dict[str, ProjectConfig] = Field(default_factory=dict)


def config_path() -> Path:
    """Return the config file path, ensuring the directory exists."""
    config_dir = CcStorage.tool_config("posthog")
    CcStorage.ensure(config_dir)
    return config_dir / "config.json"


def load_config() -> PostHogConfig:
    """Load config from disk. Returns empty config if file doesn't exist."""
    path = config_path()
    if not path.exists():
        return PostHogConfig()
    data = json.loads(path.read_text(encoding="utf-8"))
    return PostHogConfig.model_validate(data)


def save_config(config: PostHogConfig) -> None:
    """Save config to disk."""
    path = config_path()
    path.write_text(
        config.model_dump_json(indent=2),
        encoding="utf-8",
    )


def get_project(config: PostHogConfig, name: Optional[str] = None) -> tuple[str, ProjectConfig]:
    """Resolve project by name or default. Raises typer.BadParameter on failure."""
    import typer

    if not config.projects:
        raise typer.BadParameter(
            "No projects configured. Run 'cc-posthog init' first."
        )

    project_name = name or config.default_project
    if not project_name:
        raise typer.BadParameter(
            "No default project set. Use --project or run 'cc-posthog init'."
        )

    if project_name not in config.projects:
        available = ", ".join(config.projects.keys())
        raise typer.BadParameter(
            f"Project '{project_name}' not found. Available: {available}"
        )

    return project_name, config.projects[project_name]
