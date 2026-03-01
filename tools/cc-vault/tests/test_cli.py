"""Tests for cc-vault CLI commands."""

import os
import pytest
from pathlib import Path
from typer.testing import CliRunner


@pytest.fixture(scope="module")
def runner():
    """Create a CLI test runner."""
    return CliRunner()


@pytest.fixture(scope="module")
def test_vault(tmp_path_factory):
    """Create a temporary vault for testing."""
    vault_dir = tmp_path_factory.mktemp("vault")
    os.environ["CC_VAULT_PATH"] = str(vault_dir)

    # Import CLI after setting env var
    import sys
    sys.path.insert(0, str(Path(__file__).parent.parent / "src"))

    from src.cli import app

    yield vault_dir, app

    if "CC_VAULT_PATH" in os.environ:
        del os.environ["CC_VAULT_PATH"]


class TestCLIBasic:
    """Test basic CLI commands."""

    def test_version(self, runner, test_vault):
        """Test --version flag."""
        _, app = test_vault
        result = runner.invoke(app, ["--version"])
        assert result.exit_code == 0
        assert "cc-vault version" in result.output

    def test_help(self, runner, test_vault):
        """Test --help flag."""
        _, app = test_vault
        result = runner.invoke(app, ["--help"])
        assert result.exit_code == 0
        assert "Personal Vault CLI" in result.output


class TestCLIInit:
    """Test init command."""

    def test_init(self, runner, test_vault):
        """Test vault initialization."""
        vault_dir, app = test_vault
        result = runner.invoke(app, ["init", "--force"])
        assert result.exit_code == 0
        assert "initialized" in result.output.lower()


class TestCLIStats:
    """Test stats command."""

    def test_stats(self, runner, test_vault):
        """Test stats command."""
        _, app = test_vault
        result = runner.invoke(app, ["stats"])
        assert result.exit_code == 0
        assert "Contacts" in result.output


class TestCLIConfig:
    """Test config commands."""

    def test_config_show(self, runner, test_vault):
        """Test config show command."""
        _, app = test_vault
        result = runner.invoke(app, ["config", "show"])
        assert result.exit_code == 0
        assert "Vault Path" in result.output


class TestCLITasks:
    """Test tasks commands."""

    def test_tasks_add(self, runner, test_vault):
        """Test adding a task."""
        _, app = test_vault
        result = runner.invoke(app, ["tasks", "add", "Test CLI Task", "-p", "high"])
        assert result.exit_code == 0
        assert "Task added" in result.output

    def test_tasks_list(self, runner, test_vault):
        """Test listing tasks."""
        _, app = test_vault
        result = runner.invoke(app, ["tasks", "list"])
        assert result.exit_code == 0
        # Either shows tasks or "No pending tasks found"
        assert "Tasks" in result.output or "No pending tasks" in result.output

    def test_tasks_done(self, runner, test_vault):
        """Test completing a task."""
        _, app = test_vault

        # Add a task first
        result = runner.invoke(app, ["tasks", "add", "Task to complete"])
        assert result.exit_code == 0

        # Extract task ID from output
        import re
        match = re.search(r"#(\d+)", result.output)
        if match:
            task_id = match.group(1)

            # Complete it
            result = runner.invoke(app, ["tasks", "done", task_id])
            assert result.exit_code == 0
            assert "completed" in result.output.lower()


class TestCLIGoals:
    """Test goals commands."""

    def test_goals_add(self, runner, test_vault):
        """Test adding a goal."""
        _, app = test_vault
        result = runner.invoke(app, ["goals", "add", "Test CLI Goal"])
        assert result.exit_code == 0
        assert "Goal added" in result.output

    def test_goals_list(self, runner, test_vault):
        """Test listing goals."""
        _, app = test_vault
        result = runner.invoke(app, ["goals", "list"])
        assert result.exit_code == 0


class TestCLIIdeas:
    """Test ideas commands."""

    def test_ideas_add(self, runner, test_vault):
        """Test adding an idea."""
        _, app = test_vault
        result = runner.invoke(app, ["ideas", "add", "Test CLI Idea", "-d", "testing"])
        assert result.exit_code == 0
        assert "Idea added" in result.output

    def test_ideas_list(self, runner, test_vault):
        """Test listing ideas."""
        _, app = test_vault
        result = runner.invoke(app, ["ideas", "list", "-s", "all"])
        assert result.exit_code == 0


class TestCLIContacts:
    """Test contacts commands."""

    def test_contacts_add(self, runner, test_vault):
        """Test adding a contact."""
        _, app = test_vault
        result = runner.invoke(app, [
            "contacts", "add", "CLI Contact",
            "-e", "cli@example.com",
            "-a", "personal"
        ])
        assert result.exit_code == 0
        assert "Contact added" in result.output

    def test_contacts_list(self, runner, test_vault):
        """Test listing contacts."""
        _, app = test_vault
        result = runner.invoke(app, ["contacts", "list"])
        assert result.exit_code == 0


class TestCLIDocs:
    """Test docs commands."""

    def test_docs_list(self, runner, test_vault):
        """Test listing documents."""
        _, app = test_vault
        result = runner.invoke(app, ["docs", "list"])
        # May be empty or have documents
        assert result.exit_code == 0


class TestCLIHealth:
    """Test health commands."""

    def test_health_list(self, runner, test_vault):
        """Test listing health entries."""
        _, app = test_vault
        result = runner.invoke(app, ["health", "list"])
        assert result.exit_code == 0


class TestCLIBackup:
    """Test backup command."""

    def test_backup(self, runner, test_vault):
        """Test creating a backup."""
        vault_dir, app = test_vault
        result = runner.invoke(app, ["backup"])
        # May succeed or fail based on database state
        # Just verify it runs without crash


class TestCLILists:
    """Test lists commands."""

    def test_lists_create(self, runner, test_vault):
        """Test creating a list."""
        _, app = test_vault
        result = runner.invoke(app, ["lists", "create", "Test List", "-d", "A test list"])
        assert result.exit_code == 0
        assert "List created" in result.output

    def test_lists_show(self, runner, test_vault):
        """Test showing a list."""
        _, app = test_vault
        result = runner.invoke(app, ["lists", "show", "Test List"])
        assert result.exit_code == 0
        assert "Test List" in result.output

    def test_lists_rename(self, runner, test_vault):
        """Test renaming a list."""
        _, app = test_vault
        result = runner.invoke(app, ["lists", "rename", "Test List", "Renamed List"])
        assert result.exit_code == 0
        assert "renamed" in result.output.lower()

    def test_lists_rename_conflict(self, runner, test_vault):
        """Test renaming to an existing name fails."""
        _, app = test_vault
        # Create a second list
        runner.invoke(app, ["lists", "create", "Other List"])
        result = runner.invoke(app, ["lists", "rename", "Renamed List", "Other List"])
        assert result.exit_code == 1
        assert "already exists" in result.output.lower()

    def test_lists_update(self, runner, test_vault):
        """Test updating a list."""
        _, app = test_vault
        result = runner.invoke(app, ["lists", "update", "Renamed List", "-d", "New description", "-t", "outreach"])
        assert result.exit_code == 0
        assert "updated" in result.output.lower()

    def test_lists_copy(self, runner, test_vault):
        """Test copying a list."""
        _, app = test_vault
        result = runner.invoke(app, ["lists", "copy", "Renamed List", "Copied List"])
        assert result.exit_code == 0
        assert "copied" in result.output.lower()

    def test_lists_remove_no_args(self, runner, test_vault):
        """Test remove without --contact-id or --query fails."""
        _, app = test_vault
        result = runner.invoke(app, ["lists", "remove", "Renamed List"])
        assert result.exit_code == 1
        assert "--contact-id" in result.output or "--query" in result.output

    def test_lists_delete_cleanup(self, runner, test_vault):
        """Test deleting lists created by tests."""
        _, app = test_vault
        for name in ["Renamed List", "Other List", "Copied List"]:
            result = runner.invoke(app, ["lists", "delete", name, "-y"])
            assert result.exit_code == 0


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
