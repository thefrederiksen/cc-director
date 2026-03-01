"""HTTP client wrapper for cc-browser daemon."""

import httpx
import json
import os
import time
from pathlib import Path
from typing import Optional


class BrowserError(Exception):
    """Error from cc-browser daemon."""
    pass


class WorkspaceError(Exception):
    """Error resolving browser workspace."""
    pass


def get_cc_browser_dir() -> Path:
    """Get cc-browser workspaces directory."""
    local_app_data = os.environ.get("LOCALAPPDATA", "")
    if not local_app_data:
        raise WorkspaceError(
            "LOCALAPPDATA environment variable not set. "
            "Cannot locate cc-browser workspaces."
        )
    return Path(local_app_data) / "cc-browser"


def resolve_workspace(workspace_name: str) -> dict:
    """Resolve workspace name or alias to workspace config."""
    cc_browser_dir = get_cc_browser_dir()

    if not cc_browser_dir.exists():
        raise WorkspaceError(
            f"cc-browser directory not found: {cc_browser_dir}\n"
            "Install cc-browser and create a workspace first.\n"
            "Run: cc-browser start --workspace research"
        )

    for workspace_dir in cc_browser_dir.iterdir():
        if not workspace_dir.is_dir():
            continue

        workspace_json = workspace_dir / "workspace.json"
        if not workspace_json.exists():
            continue

        try:
            with open(workspace_json, "r") as f:
                config = json.load(f)
        except (json.JSONDecodeError, IOError):
            continue

        if workspace_dir.name == workspace_name:
            return config

        browser = config.get("browser", "")
        workspace = config.get("workspace", "")
        if f"{browser}-{workspace}" == workspace_name:
            return config

        aliases = config.get("aliases", [])
        if workspace_name in aliases:
            return config

    available = []
    for workspace_dir in cc_browser_dir.iterdir():
        if workspace_dir.is_dir():
            workspace_json = workspace_dir / "workspace.json"
            if workspace_json.exists():
                try:
                    with open(workspace_json, "r") as f:
                        config = json.load(f)
                    aliases = config.get("aliases", [])
                    available.append(f"{workspace_dir.name} (aliases: {', '.join(aliases)})")
                except (json.JSONDecodeError, IOError):
                    available.append(workspace_dir.name)

    available_str = "\n  - ".join(available) if available else "(none found)"
    raise WorkspaceError(
        f"Workspace '{workspace_name}' not found.\n\n"
        f"Available workspaces:\n  - {available_str}\n\n"
        "Start a workspace with: cc-browser start --workspace research"
    )


def get_port_for_workspace(workspace_name: str) -> int:
    """Get daemon port for a workspace name or alias."""
    config = resolve_workspace(workspace_name)

    port = config.get("daemonPort")
    if not port:
        raise WorkspaceError(
            f"Workspace '{workspace_name}' has no daemonPort configured.\n"
            "Edit the workspace.json and add a daemonPort field."
        )

    return port


class BrowserClient:
    """HTTP client for cc-browser daemon.

    Communicates with the cc-browser daemon on localhost.
    All browser interactions go through this client.
    """

    def __init__(self, workspace: str, timeout: float = 30.0):
        self.workspace = workspace
        self.port = get_port_for_workspace(workspace)
        self.base_url = f"http://localhost:{self.port}"
        self.timeout = timeout
        self._client = httpx.Client(timeout=timeout)

    def _post(self, endpoint: str, data: Optional[dict] = None) -> dict:
        """Send POST request to daemon."""
        try:
            response = self._client.post(
                f"{self.base_url}{endpoint}",
                json=data or {}
            )
            result = response.json()

            if not result.get("success", False):
                raise BrowserError(result.get("error", "Unknown error"))

            return result
        except httpx.ConnectError:
            raise BrowserError(
                f"Cannot connect to cc-browser daemon on port {self.port}.\n"
                f"Start it with: cc-browser start --workspace {self.workspace}"
            )
        except httpx.TimeoutException:
            raise BrowserError(f"Request timed out after {self.timeout}s")

    def _get(self, endpoint: str) -> dict:
        """Send GET request to daemon."""
        try:
            response = self._client.get(f"{self.base_url}{endpoint}")
            result = response.json()

            if not result.get("success", False):
                raise BrowserError(result.get("error", "Unknown error"))

            return result
        except httpx.ConnectError:
            raise BrowserError(
                f"Cannot connect to cc-browser daemon on port {self.port}.\n"
                f"Start it with: cc-browser start --workspace {self.workspace}"
            )
        except httpx.TimeoutException:
            raise BrowserError(f"Request timed out after {self.timeout}s")

    def status(self) -> dict:
        return self._get("/")

    def navigate(self, url: str, wait_until: str = "load") -> dict:
        return self._post("/navigate", {"url": url, "waitUntil": wait_until})

    def snapshot(self, interactive: bool = True) -> dict:
        return self._post("/snapshot", {"interactive": interactive})

    def info(self) -> dict:
        return self._post("/info")

    def text(self, selector: Optional[str] = None) -> dict:
        data = {}
        if selector:
            data["selector"] = selector
        return self._post("/text", data)

    def html(self, selector: Optional[str] = None) -> dict:
        data = {}
        if selector:
            data["selector"] = selector
        return self._post("/html", data)

    def click(self, ref: str) -> dict:
        return self._post("/click", {"ref": ref})

    def type_text(self, ref: str, text: str) -> dict:
        return self._post("/type", {"ref": ref, "text": text})

    def press(self, key: str) -> dict:
        return self._post("/press", {"key": key})

    def scroll(self, direction: str = "down") -> dict:
        return self._post("/scroll", {"direction": direction})

    def evaluate(self, js: str) -> dict:
        return self._post("/evaluate", {"js": js})

    def wait(self, ms: int) -> dict:
        return self._post("/wait", {"time": ms})

    def wait_for_text(self, text: str, timeout: int = 5000) -> dict:
        return self._post("/wait", {"text": text, "timeout": timeout})

    def captcha_detect(self) -> dict:
        """Detect CAPTCHA on current page. Returns {detected, type, ...}."""
        return self._post("/captcha/detect")

    def captcha_solve(self, max_attempts: int = 3) -> dict:
        """Attempt to solve CAPTCHA on current page.

        Returns {solved, message, type, attempts}.
        """
        return self._post("/captcha/solve", {"attempts": max_attempts})

    def close(self):
        self._client.close()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()
