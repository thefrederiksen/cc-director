"""Session management for cc-crawl4ai."""

import json
import shutil
from pathlib import Path
from typing import Optional
from dataclasses import dataclass, asdict
from datetime import datetime


def get_sessions_dir() -> Path:
    """Get the sessions directory."""
    sessions_dir = Path.home() / ".cc-crawl4ai" / "sessions"
    sessions_dir.mkdir(parents=True, exist_ok=True)
    return sessions_dir


def get_cache_dir() -> Path:
    """Get the cache directory."""
    cache_dir = Path.home() / ".cc-crawl4ai" / "cache"
    cache_dir.mkdir(parents=True, exist_ok=True)
    return cache_dir


@dataclass
class SessionInfo:
    """Session metadata."""
    name: str
    created_at: str
    last_used: str
    url: Optional[str] = None
    browser: str = "chromium"
    description: Optional[str] = None


class SessionManager:
    """Manage browser sessions with persistent state."""

    def __init__(self) -> None:
        self.sessions_dir = get_sessions_dir()

    def _session_path(self, name: str) -> Path:
        """Get path to session directory."""
        return self.sessions_dir / name

    def _info_path(self, name: str) -> Path:
        """Get path to session info file."""
        return self._session_path(name) / "session.json"

    def _profile_path(self, name: str) -> Path:
        """Get path to browser profile directory."""
        return self._session_path(name) / "profile"

    def exists(self, name: str) -> bool:
        """Check if session exists."""
        return self._info_path(name).exists()

    def create(
        self,
        name: str,
        url: Optional[str] = None,
        browser: str = "chromium",
        description: Optional[str] = None,
    ) -> SessionInfo:
        """Create a new session."""
        session_dir = self._session_path(name)
        session_dir.mkdir(parents=True, exist_ok=True)

        # Create profile directory
        profile_dir = self._profile_path(name)
        profile_dir.mkdir(parents=True, exist_ok=True)

        now = datetime.now().isoformat()
        info = SessionInfo(
            name=name,
            created_at=now,
            last_used=now,
            url=url,
            browser=browser,
            description=description,
        )

        with open(self._info_path(name), "w") as f:
            json.dump(asdict(info), f, indent=2)

        return info

    def get(self, name: str) -> Optional[SessionInfo]:
        """Get session info."""
        info_path = self._info_path(name)
        if not info_path.exists():
            return None

        with open(info_path) as f:
            data = json.load(f)

        return SessionInfo(**data)

    def get_profile_path(self, name: str) -> Optional[Path]:
        """Get browser profile path for session."""
        if not self.exists(name):
            return None
        return self._profile_path(name)

    def update_last_used(self, name: str) -> None:
        """Update last used timestamp."""
        info = self.get(name)
        if info:
            info.last_used = datetime.now().isoformat()
            with open(self._info_path(name), "w") as f:
                json.dump(asdict(info), f, indent=2)

    def list_sessions(self) -> list[SessionInfo]:
        """List all sessions."""
        sessions = []
        for session_dir in self.sessions_dir.iterdir():
            if session_dir.is_dir():
                info = self.get(session_dir.name)
                if info:
                    sessions.append(info)
        return sorted(sessions, key=lambda s: s.last_used, reverse=True)

    def delete(self, name: str) -> bool:
        """Delete a session."""
        session_dir = self._session_path(name)
        if session_dir.exists():
            shutil.rmtree(session_dir)
            return True
        return False

    def rename(self, old_name: str, new_name: str) -> bool:
        """Rename a session."""
        old_path = self._session_path(old_name)
        new_path = self._session_path(new_name)

        if not old_path.exists() or new_path.exists():
            return False

        old_path.rename(new_path)

        # Update session info
        info = self.get(new_name)
        if info:
            info.name = new_name
            with open(self._info_path(new_name), "w") as f:
                json.dump(asdict(info), f, indent=2)

        return True
