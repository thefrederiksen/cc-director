"""HTTP client wrapper for cc-browser daemon."""

import httpx
import json
import os
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
    """Resolve workspace name or alias to workspace config.

    Scans all cc-browser workspace directories for matching workspace name or alias.

    Args:
        workspace_name: Workspace name or alias (e.g., "spotify", "work", "edge-personal")

    Returns:
        Workspace config dict with browser, workspace, daemonPort, etc.

    Raises:
        WorkspaceError: If workspace cannot be found or resolved.
    """
    cc_browser_dir = get_cc_browser_dir()

    if not cc_browser_dir.exists():
        raise WorkspaceError(
            f"cc-browser directory not found: {cc_browser_dir}\n"
            "Install cc-browser and create a workspace first.\n"
            "Run: cc-browser start --workspace work"
        )

    # Scan all workspace directories
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

        # Check if workspace name matches directory name
        if workspace_dir.name == workspace_name:
            return config

        # Check if workspace name matches browser-workspace combo
        browser = config.get("browser", "")
        workspace = config.get("workspace", "")
        if f"{browser}-{workspace}" == workspace_name:
            return config

        # Check aliases
        aliases = config.get("aliases", [])
        if workspace_name in aliases:
            return config

    # Workspace not found - provide helpful error
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
        "Use 'cc-spotify config --workspace <name>' to set a workspace."
    )


def get_port_for_workspace(workspace_name: str) -> int:
    """Get daemon port for a workspace name or alias.

    Args:
        workspace_name: Workspace name or alias

    Returns:
        Daemon port number

    Raises:
        WorkspaceError: If workspace not found or has no daemonPort.
    """
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
    Workspace is resolved to get the daemon port.
    """

    def __init__(self, workspace: str = None, timeout: float = 30.0):
        """Initialize browser client for a workspace.

        Args:
            workspace: Workspace name or alias (e.g., "spotify", "work", "edge-personal")
            timeout: HTTP request timeout in seconds

        Raises:
            WorkspaceError: If workspace cannot be resolved.
        """
        self.workspace = workspace
        self.port = get_port_for_workspace(self.workspace)
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
                f"Start it with: cc-browser daemon --workspace {self.workspace}"
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
                f"Start it with: cc-browser daemon --workspace {self.workspace}"
            )
        except httpx.TimeoutException:
            raise BrowserError(f"Request timed out after {self.timeout}s")

    def status(self) -> dict:
        """Get daemon and browser status."""
        return self._get("/")

    def navigate(self, url: str) -> dict:
        """Navigate to URL."""
        return self._post("/navigate", {"url": url})

    def info(self) -> dict:
        """Get current page info (URL, title)."""
        return self._post("/info")

    def snapshot(self, interactive: bool = True) -> dict:
        """Get page snapshot with element refs."""
        return self._post("/snapshot", {"interactive": interactive})

    def click(self, ref: str) -> dict:
        """Click element by ref."""
        return self._post("/click", {"ref": ref})

    def type(self, ref: str, text: str) -> dict:
        """Type text into element."""
        return self._post("/type", {"ref": ref, "text": text})

    def press(self, key: str) -> dict:
        """Press keyboard key."""
        return self._post("/press", {"key": key})

    def evaluate(self, js: str) -> dict:
        """Execute JavaScript."""
        return self._post("/evaluate", {"js": js})

    def scroll(self, direction: str = "down", ref: Optional[str] = None,
               amount: Optional[int] = None) -> dict:
        """Scroll page or element.

        Args:
            direction: "up", "down", "left", "right"
            ref: Element ref to scroll into view (overrides direction scroll)
            amount: Pixels to scroll (default 500 in daemon). In human mode,
                    the daemon breaks this into 3-6 smaller wheel events with
                    random delays, simulating real mouse wheel behavior.
        """
        data = {"direction": direction}
        if ref:
            data["ref"] = ref
        if amount is not None:
            data["amount"] = amount
        return self._post("/scroll", data)

    def text(self, selector: Optional[str] = None) -> dict:
        """Get page text content."""
        data = {}
        if selector:
            data["selector"] = selector
        return self._post("/text", data)

    def wait(self, ms: int) -> dict:
        """Wait for specified time."""
        return self._post("/wait", {"time": ms})

    def wait_for_text(self, text: str, timeout: int = 5000) -> dict:
        """Wait for text to appear."""
        return self._post("/wait", {"text": text, "timeout": timeout})

    def close(self):
        """Close HTTP client."""
        self._client.close()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


# Convenience function for quick operations
def get_client(workspace: str) -> BrowserClient:
    """Get a browser client instance for a workspace."""
    return BrowserClient(workspace=workspace)
