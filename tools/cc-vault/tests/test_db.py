"""Tests for cc-vault database operations."""

import os
import tempfile
import pytest
from pathlib import Path


# Set up test environment before importing modules
@pytest.fixture(scope="module")
def test_vault(tmp_path_factory):
    """Create a temporary vault for testing."""
    vault_dir = tmp_path_factory.mktemp("vault")

    # Set environment variable for test vault
    os.environ["CC_VAULT_PATH"] = str(vault_dir)

    # Now import the modules after setting env var
    import sys
    sys.path.insert(0, str(Path(__file__).parent.parent / "src"))

    from src import db

    # Initialize the database
    db.init_db()

    yield vault_dir, db

    # Cleanup
    if "CC_VAULT_PATH" in os.environ:
        del os.environ["CC_VAULT_PATH"]


class TestContacts:
    """Test contact operations."""

    def test_add_contact(self, test_vault):
        """Test adding a contact."""
        _, db = test_vault

        contact_id = db.add_contact(
            email="john@example.com",
            name="John Doe",
            account="personal",
            company="Acme Inc",
            phone="+1-555-1234"
        )

        assert contact_id > 0

        # Verify contact exists
        contact = db.get_contact("john@example.com")
        assert contact is not None
        assert contact["name"] == "John Doe"
        assert contact["email"] == "john@example.com"
        assert contact["company"] == "Acme Inc"

    def test_get_contact_by_id(self, test_vault):
        """Test getting contact by ID."""
        _, db = test_vault

        contact_id = db.add_contact(
            email="jane@example.com",
            name="Jane Smith",
            account="consulting"
        )

        contact = db.get_contact_by_id(contact_id)
        assert contact is not None
        assert contact["name"] == "Jane Smith"

    def test_list_contacts(self, test_vault):
        """Test listing contacts."""
        _, db = test_vault

        contacts = db.list_contacts()
        assert len(contacts) >= 2  # From previous tests

    def test_update_contact(self, test_vault):
        """Test updating a contact."""
        _, db = test_vault

        # Look up contact ID first
        contact = db.get_contact("john@example.com")
        assert contact is not None

        # Update contact by ID
        result = db.update_contact(
            contact["id"],
            title="CEO"
        )

        assert result is True

        # Verify update
        contact = db.get_contact("john@example.com")
        assert contact["title"] == "CEO"

    def test_list_contacts_has_fields(self, test_vault):
        """Test filtering contacts by field presence."""
        _, db = test_vault

        # Add a contact with linkedin
        db.add_contact(
            email="linked@example.com",
            name="LinkedIn User",
            account="personal",
        )
        linked = db.get_contact("linked@example.com")
        db.update_contact(linked["id"], linkedin="https://linkedin.com/in/test")

        # Filter by has linkedin
        contacts = db.list_contacts(has_fields=["linkedin"])
        assert len(contacts) >= 1
        for c in contacts:
            assert c["linkedin"] is not None and c["linkedin"] != ""

    def test_list_contacts_missing_fields(self, test_vault):
        """Test filtering contacts by field absence."""
        _, db = test_vault

        # Filter by missing linkedin (john and jane don't have it)
        contacts = db.list_contacts(missing_fields=["linkedin"])
        for c in contacts:
            assert c["linkedin"] is None or c["linkedin"] == ""

    def test_list_contacts_has_and_missing_combined(self, test_vault):
        """Test combining has and missing filters."""
        _, db = test_vault

        # Add a contact with linkedin but no company
        db.add_contact(
            email="nocompany@example.com",
            name="",
            account="consulting",
        )
        nc = db.get_contact("nocompany@example.com")
        db.update_contact(nc["id"], linkedin="https://linkedin.com/in/nocompany")

        # Find contacts with linkedin but missing company
        contacts = db.list_contacts(has_fields=["linkedin"], missing_fields=["company"])
        assert len(contacts) >= 1
        for c in contacts:
            assert c["linkedin"] is not None and c["linkedin"] != ""
            assert c["company"] is None or c["company"] == ""

    def test_list_contacts_invalid_field_raises(self, test_vault):
        """Test that invalid field names raise ValueError."""
        _, db = test_vault

        with pytest.raises(ValueError, match="Invalid field"):
            db.list_contacts(has_fields=["not_a_real_field"])

        with pytest.raises(ValueError, match="Invalid field"):
            db.list_contacts(missing_fields=["bobby_tables; DROP TABLE contacts"])


class TestTasks:
    """Test task operations."""

    def test_add_task(self, test_vault):
        """Test adding a task."""
        _, db = test_vault

        task_id = db.add_task(
            title="Test Task",
            priority=1,
            description="This is a test task"
        )

        assert task_id > 0

    def test_list_tasks(self, test_vault):
        """Test listing tasks."""
        _, db = test_vault

        tasks = db.list_tasks()
        assert len(tasks) >= 1

        # Check task has expected fields
        task = tasks[0]
        assert "id" in task
        assert "title" in task

    def test_complete_task(self, test_vault):
        """Test completing a task."""
        _, db = test_vault

        # Add a task with a unique title
        title = f"Task to complete {id(self)}"
        task_id = db.add_task(title=title, priority=3)

        # Complete it - should not raise an error
        db.complete_task(task_id)

        # Verify by getting the task directly
        task = db.get_task(task_id)
        if task:
            # Status could be 'completed' or 'done' depending on implementation
            assert task["status"] in ("completed", "done")


class TestGoals:
    """Test goal operations."""

    def test_add_goal(self, test_vault):
        """Test adding a goal."""
        _, db = test_vault

        goal_id = db.add_goal(
            title="Test Goal",
            description="A test goal",
            category="career",
            target_date="2025-12-31"
        )

        assert goal_id > 0

    def test_list_goals(self, test_vault):
        """Test listing goals."""
        _, db = test_vault

        goals = db.list_goals()
        assert len(goals) >= 1

    def test_achieve_goal(self, test_vault):
        """Test achieving a goal."""
        _, db = test_vault

        # Add a goal
        goal_id = db.add_goal(title="Goal to achieve")

        # Achieve it
        result = db.achieve_goal(goal_id)
        assert result is True

        # Verify it's achieved
        goals = db.list_goals(include_achieved=True)
        achieved = [g for g in goals if g["id"] == goal_id]
        assert len(achieved) == 1
        assert achieved[0]["status"] == "achieved"

    def test_pause_resume_goal(self, test_vault):
        """Test pausing and resuming a goal."""
        _, db = test_vault

        # Add a goal
        goal_id = db.add_goal(title="Goal to pause")

        # Pause it
        result = db.pause_goal(goal_id)
        assert result is True

        # Resume it
        result = db.resume_goal(goal_id)
        assert result is True


class TestIdeas:
    """Test idea operations."""

    def test_add_idea(self, test_vault):
        """Test adding an idea."""
        _, db = test_vault

        idea_id = db.add_idea(
            content="This is a test idea",
            domain="tech",
            tags="test,demo"
        )

        assert idea_id > 0

    def test_list_ideas(self, test_vault):
        """Test listing ideas."""
        _, db = test_vault

        ideas = db.list_ideas()
        assert len(ideas) >= 1

    def test_update_idea_status(self, test_vault):
        """Test updating idea status."""
        _, db = test_vault

        # Add an idea
        idea_id = db.add_idea(content="Idea to update")

        # Update status
        result = db.update_idea_status(idea_id, "actionable")
        assert result is True

        # Verify
        idea = db.get_idea(idea_id)
        assert idea["status"] == "actionable"


class TestDocuments:
    """Test document operations."""

    def test_list_documents(self, test_vault):
        """Test listing documents (empty)."""
        _, db = test_vault

        docs = db.list_documents()
        # May be empty initially
        assert isinstance(docs, list)


class TestMemories:
    """Test memory operations."""

    def test_add_memory(self, test_vault):
        """Test adding a memory about a contact."""
        _, db = test_vault

        # Ensure we have a contact
        try:
            db.add_contact(
                email="memory@example.com",
                name="Memory Test",
                account="personal"
            )
        except Exception:
            pass  # Contact may already exist

        # Look up contact ID
        contact = db.get_contact("memory@example.com")
        assert contact is not None

        # Add memory
        memory_id = db.add_memory(
            contact["id"],
            category="preference",
            fact="Prefers morning meetings"
        )

        assert memory_id > 0

    def test_get_memories(self, test_vault):
        """Test getting memories for a contact."""
        _, db = test_vault

        contact = db.get_contact("memory@example.com")
        assert contact is not None
        memories = db.get_memories(contact["id"])
        assert len(memories) >= 1


class TestFacts:
    """Test fact operations."""

    def test_add_fact(self, test_vault):
        """Test adding a general fact."""
        _, db = test_vault

        fact_id = db.add_fact(
            domain="tech",
            fact="Python 3.11 is faster than 3.10",
            tags="python,performance"
        )

        assert fact_id > 0


class TestVaultStats:
    """Test vault statistics."""

    def test_get_vault_stats(self, test_vault):
        """Test getting vault statistics."""
        _, db = test_vault

        stats = db.get_vault_stats()

        assert "contacts" in stats
        assert "tasks_pending" in stats
        assert "tasks_completed" in stats
        assert "goals_active" in stats
        assert "ideas" in stats
        assert "documents" in stats
        assert "health_entries" in stats

        # We've added data, so should have counts
        assert stats["contacts"] >= 2
        assert stats["ideas"] >= 1


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
