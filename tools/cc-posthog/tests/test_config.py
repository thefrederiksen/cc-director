"""Tests for cc-posthog config module."""

import json
import sys
from pathlib import Path

import pytest

# Add src to path
sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))
sys.path.insert(0, str(Path(__file__).parent.parent.parent / "cc_shared"))

from src.config import PostHogConfig, ProjectConfig, load_config, save_config, config_path


@pytest.fixture
def tmp_config(tmp_path, monkeypatch):
    """Redirect config storage to a temp directory."""
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(tmp_path / "cc-director"))
    return tmp_path


class TestPostHogConfig:

    def test_empty_config(self):
        config = PostHogConfig()
        assert config.api_key == ""
        assert config.default_project == ""
        assert config.projects == {}

    def test_config_with_project(self):
        proj = ProjectConfig(project_id=12345, host="https://us.posthog.com")
        config = PostHogConfig(
            api_key="phx_test123",
            default_project="mysite",
            projects={"mysite": proj},
        )
        assert config.api_key == "phx_test123"
        assert config.projects["mysite"].project_id == 12345

    def test_project_with_funnel_steps(self):
        proj = ProjectConfig(
            project_id=99,
            funnel_steps=["homepage_viewed", "signup_clicked", "payment_completed"],
        )
        assert len(proj.funnel_steps) == 3
        assert proj.funnel_steps[0] == "homepage_viewed"


class TestConfigPersistence:

    def test_save_load_roundtrip(self, tmp_config):
        proj = ProjectConfig(
            project_id=42,
            host="https://eu.posthog.com",
            funnel_steps=["step1", "step2"],
        )
        config = PostHogConfig(
            api_key="phx_roundtrip",
            default_project="testsite",
            projects={"testsite": proj},
        )
        save_config(config)
        loaded = load_config()
        assert loaded.api_key == "phx_roundtrip"
        assert loaded.default_project == "testsite"
        assert loaded.projects["testsite"].project_id == 42
        assert loaded.projects["testsite"].host == "https://eu.posthog.com"
        assert loaded.projects["testsite"].funnel_steps == ["step1", "step2"]

    def test_load_missing_file_returns_empty(self, tmp_config):
        config = load_config()
        assert config.api_key == ""
        assert config.projects == {}

    def test_add_multiple_projects(self, tmp_config):
        config = PostHogConfig(api_key="phx_multi")
        config.projects["site-a"] = ProjectConfig(project_id=1)
        config.projects["site-b"] = ProjectConfig(project_id=2, host="https://eu.posthog.com")
        config.default_project = "site-a"
        save_config(config)

        loaded = load_config()
        assert len(loaded.projects) == 2
        assert loaded.projects["site-a"].project_id == 1
        assert loaded.projects["site-b"].project_id == 2
        assert loaded.projects["site-b"].host == "https://eu.posthog.com"


class TestGetProject:

    def test_get_default_project(self, tmp_config):
        from src.config import get_project

        config = PostHogConfig(
            api_key="key",
            default_project="main",
            projects={"main": ProjectConfig(project_id=10)},
        )
        name, proj = get_project(config)
        assert name == "main"
        assert proj.project_id == 10

    def test_get_named_project(self, tmp_config):
        from src.config import get_project

        config = PostHogConfig(
            api_key="key",
            default_project="main",
            projects={
                "main": ProjectConfig(project_id=10),
                "other": ProjectConfig(project_id=20),
            },
        )
        name, proj = get_project(config, "other")
        assert name == "other"
        assert proj.project_id == 20

    def test_get_missing_project_raises(self, tmp_config):
        import typer
        from src.config import get_project

        config = PostHogConfig(
            api_key="key",
            default_project="main",
            projects={"main": ProjectConfig(project_id=10)},
        )
        with pytest.raises(typer.BadParameter, match="not found"):
            get_project(config, "nonexistent")

    def test_no_projects_raises(self, tmp_config):
        import typer
        from src.config import get_project

        config = PostHogConfig(api_key="key")
        with pytest.raises(typer.BadParameter, match="No projects configured"):
            get_project(config)
