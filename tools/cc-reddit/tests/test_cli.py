"""Tests for cc-reddit CLI."""

import pytest
from typer.testing import CliRunner
from src.cli import app

runner = CliRunner()


def test_app_help():
    """Test that help command works."""
    result = runner.invoke(app, ["--help"])
    assert result.exit_code == 0
    assert "Reddit CLI via browser automation" in result.stdout


def test_status_no_daemon():
    """Test status command when daemon not running."""
    result = runner.invoke(app, ["status"])
    # Should fail gracefully when daemon not running
    assert result.exit_code == 1 or "Cannot connect" in result.stdout


def test_whoami_help():
    """Test whoami command help."""
    result = runner.invoke(app, ["whoami", "--help"])
    assert result.exit_code == 0


def test_feed_help():
    """Test feed command help."""
    result = runner.invoke(app, ["feed", "--help"])
    assert result.exit_code == 0
    assert "subreddit" in result.stdout.lower()


def test_upvote_help():
    """Test upvote command help."""
    result = runner.invoke(app, ["upvote", "--help"])
    assert result.exit_code == 0


def test_join_help():
    """Test join command help."""
    result = runner.invoke(app, ["join", "--help"])
    assert result.exit_code == 0
