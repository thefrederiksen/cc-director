"""Tests for cc-crawl4ai session management.

Tests SessionInfo, SessionManager and its methods using tmp_path
so we never touch the real ~/.cc-crawl4ai directory.

No API calls, no browser automation, no crawl4ai imports.
"""

import sys
import json
from pathlib import Path

import pytest

# ---------------------------------------------------------------------------
# Stub crawl4ai so sessions.py can be imported without it
# ---------------------------------------------------------------------------
_pkg_src = str(Path(__file__).resolve().parent.parent / "src")

_crawl4ai_stub = type(sys)("crawl4ai")
_crawl4ai_stub.__path__ = []
sys.modules.setdefault("crawl4ai", _crawl4ai_stub)

if _pkg_src not in sys.path:
    sys.path.insert(0, _pkg_src)

from sessions import SessionInfo, SessionManager


# ---------------------------------------------------------------------------
# Fixture: a SessionManager that uses tmp_path instead of ~/.cc-crawl4ai
# ---------------------------------------------------------------------------
@pytest.fixture
def manager(tmp_path):
    """Return a SessionManager whose sessions_dir is inside tmp_path."""
    sessions_dir = tmp_path / "sessions"
    sessions_dir.mkdir()
    mgr = SessionManager.__new__(SessionManager)
    mgr.sessions_dir = sessions_dir
    return mgr


# =========================================================================
# SessionManager creates sessions directory
# =========================================================================
class TestSessionManagerInit:
    """Verify SessionManager.__init__ creates the sessions directory."""

    def test_init_creates_sessions_dir(self, tmp_path, monkeypatch):
        target_dir = tmp_path / ".cc-crawl4ai" / "sessions"
        monkeypatch.setattr(
            "sessions.get_sessions_dir", lambda: _ensure_dir(target_dir)
        )
        mgr = SessionManager()
        assert mgr.sessions_dir.exists()
        assert mgr.sessions_dir.is_dir()


def _ensure_dir(p: Path) -> Path:
    p.mkdir(parents=True, exist_ok=True)
    return p


# =========================================================================
# _session_path
# =========================================================================
class TestSessionPath:
    def test_returns_correct_path(self, manager):
        path = manager._session_path("my-session")
        assert path == manager.sessions_dir / "my-session"

    def test_different_names_give_different_paths(self, manager):
        a = manager._session_path("alpha")
        b = manager._session_path("beta")
        assert a != b


# =========================================================================
# _info_path
# =========================================================================
class TestInfoPath:
    def test_returns_session_json(self, manager):
        path = manager._info_path("test")
        assert path == manager.sessions_dir / "test" / "session.json"

    def test_is_child_of_session_path(self, manager):
        info = manager._info_path("test")
        session = manager._session_path("test")
        assert info.parent == session


# =========================================================================
# _profile_path
# =========================================================================
class TestProfilePath:
    def test_returns_profile_dir(self, manager):
        path = manager._profile_path("test")
        assert path == manager.sessions_dir / "test" / "profile"

    def test_is_child_of_session_path(self, manager):
        profile = manager._profile_path("test")
        session = manager._session_path("test")
        assert profile.parent == session


# =========================================================================
# create()
# =========================================================================
class TestCreate:
    def test_creates_session_directory(self, manager):
        manager.create("new-session")
        assert (manager.sessions_dir / "new-session").is_dir()

    def test_creates_info_file(self, manager):
        manager.create("new-session")
        info_path = manager.sessions_dir / "new-session" / "session.json"
        assert info_path.is_file()

    def test_creates_profile_directory(self, manager):
        manager.create("new-session")
        profile_path = manager.sessions_dir / "new-session" / "profile"
        assert profile_path.is_dir()

    def test_info_file_contains_name(self, manager):
        manager.create("new-session")
        info_path = manager.sessions_dir / "new-session" / "session.json"
        data = json.loads(info_path.read_text())
        assert data["name"] == "new-session"

    def test_info_file_contains_timestamps(self, manager):
        manager.create("new-session")
        info_path = manager.sessions_dir / "new-session" / "session.json"
        data = json.loads(info_path.read_text())
        assert "created_at" in data
        assert "last_used" in data
        assert len(data["created_at"]) > 0
        assert len(data["last_used"]) > 0

    def test_returns_session_info(self, manager):
        info = manager.create("new-session")
        assert isinstance(info, SessionInfo)
        assert info.name == "new-session"

    def test_with_url(self, manager):
        info = manager.create("s", url="https://example.com")
        assert info.url == "https://example.com"

    def test_with_browser(self, manager):
        info = manager.create("s", browser="firefox")
        assert info.browser == "firefox"

    def test_with_description(self, manager):
        info = manager.create("s", description="Test session")
        assert info.description == "Test session"

    def test_default_browser_chromium(self, manager):
        info = manager.create("s")
        assert info.browser == "chromium"

    def test_default_url_none(self, manager):
        info = manager.create("s")
        assert info.url is None

    def test_default_description_none(self, manager):
        info = manager.create("s")
        assert info.description is None


# =========================================================================
# exists()
# =========================================================================
class TestExists:
    def test_true_for_existing_session(self, manager):
        manager.create("present")
        assert manager.exists("present") is True

    def test_false_for_missing_session(self, manager):
        assert manager.exists("nope") is False

    def test_false_if_dir_exists_but_no_info(self, manager):
        """A directory without session.json should not count as existing."""
        (manager.sessions_dir / "empty-dir").mkdir()
        assert manager.exists("empty-dir") is False


# =========================================================================
# get()
# =========================================================================
class TestGet:
    def test_returns_session_info(self, manager):
        manager.create("s1", url="https://example.com", browser="firefox", description="desc")
        info = manager.get("s1")
        assert info is not None
        assert info.name == "s1"
        assert info.url == "https://example.com"
        assert info.browser == "firefox"
        assert info.description == "desc"

    def test_returns_none_for_missing(self, manager):
        assert manager.get("nonexistent") is None

    def test_created_at_matches(self, manager):
        created = manager.create("s1")
        fetched = manager.get("s1")
        assert fetched.created_at == created.created_at

    def test_last_used_matches(self, manager):
        created = manager.create("s1")
        fetched = manager.get("s1")
        assert fetched.last_used == created.last_used


# =========================================================================
# list_sessions()
# =========================================================================
class TestListSessions:
    def test_empty_when_none(self, manager):
        sessions = manager.list_sessions()
        assert sessions == []

    def test_returns_all_sessions(self, manager):
        manager.create("alpha")
        manager.create("beta")
        manager.create("gamma")
        sessions = manager.list_sessions()
        names = {s.name for s in sessions}
        assert names == {"alpha", "beta", "gamma"}

    def test_returns_session_info_objects(self, manager):
        manager.create("test")
        sessions = manager.list_sessions()
        assert all(isinstance(s, SessionInfo) for s in sessions)

    def test_ignores_dirs_without_info(self, manager):
        """Directories without session.json should be skipped."""
        manager.create("real")
        (manager.sessions_dir / "fake-dir").mkdir()
        sessions = manager.list_sessions()
        names = [s.name for s in sessions]
        assert "real" in names
        assert "fake-dir" not in names

    def test_sorted_by_last_used_descending(self, manager):
        """Sessions should be sorted by last_used, most recent first."""
        import time
        manager.create("old")
        time.sleep(0.05)  # ensure different timestamps
        manager.create("newer")
        time.sleep(0.05)
        manager.create("newest")
        sessions = manager.list_sessions()
        assert sessions[0].name == "newest"
        assert sessions[-1].name == "old"


# =========================================================================
# delete()
# =========================================================================
class TestDelete:
    def test_removes_session_directory(self, manager):
        manager.create("doomed")
        result = manager.delete("doomed")
        assert result is True
        assert not (manager.sessions_dir / "doomed").exists()

    def test_returns_false_for_missing(self, manager):
        result = manager.delete("nonexistent")
        assert result is False

    def test_session_no_longer_exists_after_delete(self, manager):
        manager.create("temp")
        manager.delete("temp")
        assert manager.exists("temp") is False

    def test_info_file_removed(self, manager):
        manager.create("temp")
        manager.delete("temp")
        assert not manager._info_path("temp").exists()

    def test_profile_dir_removed(self, manager):
        manager.create("temp")
        manager.delete("temp")
        assert not manager._profile_path("temp").exists()


# =========================================================================
# rename()
# =========================================================================
class TestRename:
    def test_renames_successfully(self, manager):
        manager.create("old-name")
        result = manager.rename("old-name", "new-name")
        assert result is True

    def test_old_name_gone(self, manager):
        manager.create("old-name")
        manager.rename("old-name", "new-name")
        assert manager.exists("old-name") is False

    def test_new_name_exists(self, manager):
        manager.create("old-name")
        manager.rename("old-name", "new-name")
        assert manager.exists("new-name") is True

    def test_info_updated_with_new_name(self, manager):
        manager.create("old-name")
        manager.rename("old-name", "new-name")
        info = manager.get("new-name")
        assert info is not None
        assert info.name == "new-name"

    def test_returns_false_if_old_missing(self, manager):
        result = manager.rename("ghost", "new")
        assert result is False

    def test_returns_false_if_new_exists(self, manager):
        manager.create("existing")
        manager.create("target")
        result = manager.rename("existing", "target")
        assert result is False

    def test_preserves_other_fields(self, manager):
        manager.create("old", url="https://x.com", browser="firefox", description="desc")
        manager.rename("old", "new")
        info = manager.get("new")
        assert info.url == "https://x.com"
        assert info.browser == "firefox"
        assert info.description == "desc"

    def test_profile_dir_accessible_after_rename(self, manager):
        manager.create("old")
        manager.rename("old", "new")
        assert manager._profile_path("new").is_dir()


# =========================================================================
# SessionInfo dataclass
# =========================================================================
class TestSessionInfo:
    def test_required_fields(self):
        info = SessionInfo(name="test", created_at="2026-01-01", last_used="2026-01-02")
        assert info.name == "test"
        assert info.created_at == "2026-01-01"
        assert info.last_used == "2026-01-02"

    def test_url_default_none(self):
        info = SessionInfo(name="t", created_at="x", last_used="x")
        assert info.url is None

    def test_browser_default_chromium(self):
        info = SessionInfo(name="t", created_at="x", last_used="x")
        assert info.browser == "chromium"

    def test_description_default_none(self):
        info = SessionInfo(name="t", created_at="x", last_used="x")
        assert info.description is None

    def test_full_creation(self):
        info = SessionInfo(
            name="full",
            created_at="2026-01-01",
            last_used="2026-01-02",
            url="https://example.com",
            browser="firefox",
            description="A test session",
        )
        assert info.url == "https://example.com"
        assert info.browser == "firefox"
        assert info.description == "A test session"
