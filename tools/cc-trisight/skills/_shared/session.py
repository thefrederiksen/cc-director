"""Session folder management and activity logging.

Every skill logs to the same session folder via CC_SESSION_DIR env var.
If unset, a new session is created automatically (for standalone testing).

Session layout:
    %APPDATA%/CCComputer/sessions/{timestamp}/
    ├── activity.jsonl         # Append-only log
    └── screenshots/           # Sequential: 001_143025.png, 002_143030.png, ...
"""
import json
import os
import time
from datetime import datetime


def _get_session_dir() -> str:
    """Get or create the session directory."""
    env = os.environ.get("CC_SESSION_DIR")
    if env:
        os.makedirs(env, exist_ok=True)
        os.makedirs(os.path.join(env, "screenshots"), exist_ok=True)
        return env

    appdata = os.environ.get("APPDATA", os.path.expanduser("~"))
    base = os.path.join(appdata, "CCComputer", "sessions")
    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
    session = os.path.join(base, ts)
    os.makedirs(session, exist_ok=True)
    os.makedirs(os.path.join(session, "screenshots"), exist_ok=True)
    return session


_session_dir: str | None = None
_screenshot_seq = 0


def get_session_dir() -> str:
    global _session_dir
    if _session_dir is None:
        _session_dir = _get_session_dir()
    return _session_dir


def get_screenshots_dir() -> str:
    return os.path.join(get_session_dir(), "screenshots")


def next_screenshot_path(ext: str = ".png") -> str:
    """Return the next sequential screenshot path."""
    global _screenshot_seq
    _screenshot_seq += 1
    ts = datetime.now().strftime("%H%M%S")
    name = f"{_screenshot_seq:03d}_{ts}{ext}"
    return os.path.join(get_screenshots_dir(), name)


def _append_log(entry: dict) -> None:
    """Append a JSON line to activity.jsonl."""
    entry["timestamp"] = datetime.now().isoformat()
    path = os.path.join(get_session_dir(), "activity.jsonl")
    with open(path, "a", encoding="utf-8") as f:
        f.write(json.dumps(entry) + "\n")


def log_skill_call(skill: str, args: dict) -> None:
    _append_log({"event": "skill_call", "skill": skill, "args": args})


def log_skill_result(skill: str, ok: bool, summary: str) -> None:
    _append_log({"event": "skill_result", "skill": skill, "success": ok, "summary": summary[:500]})


def log_screenshot(path: str, reason: str = "") -> None:
    _append_log({"event": "screenshot", "path": path, "reason": reason})
