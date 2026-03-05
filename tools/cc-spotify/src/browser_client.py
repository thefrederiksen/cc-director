"""HTTP client wrapper for cc-browser v2 daemon."""

import httpx
import json
import os
from pathlib import Path
from typing import Optional


class BrowserError(Exception):
    """Error from cc-browser daemon."""
    pass


class ConnectionError(Exception):
    """Error resolving browser connection."""
    pass


# Keep WorkspaceError as alias for backward compatibility in cli.py imports
WorkspaceError = ConnectionError

DEFAULT_DAEMON_PORT = 9280
TOOL_NAME = "cc-spotify"


def get_connections_dir() -> Path:
    """Get cc-director connections directory."""
    local_app_data = os.environ.get("LOCALAPPDATA", "")
    if not local_app_data:
        raise ConnectionError(
            "LOCALAPPDATA environment variable not set. "
            "Cannot locate cc-director connections."
        )
    return Path(local_app_data) / "cc-director" / "connections"


def get_connections_registry() -> Path:
    """Get connections.json path."""
    return get_connections_dir() / "connections.json"


def resolve_connection(connection_name: str = None) -> str:
    """Resolve connection name for this tool.

    Resolution order:
    1. Explicit connection_name if provided
    2. Find connection with toolBinding == TOOL_NAME in connections.json
    3. Error with instructions

    Args:
        connection_name: Explicit connection name, or None for auto-resolve

    Returns:
        Connection name string

    Raises:
        ConnectionError: If connection cannot be resolved.
    """
    if connection_name:
        # Verify it exists in registry
        registry = get_connections_registry()
        if registry.exists():
            try:
                connections = json.loads(registry.read_text())
                names = [c.get("name") for c in connections]
                if connection_name not in names:
                    available = ", ".join(names) if names else "(none)"
                    raise ConnectionError(
                        f"Connection '{connection_name}' not found.\n"
                        f"Available connections: {available}\n"
                        f"Create one with: cc-browser connections add {connection_name}"
                    )
            except (json.JSONDecodeError, IOError):
                pass
        return connection_name

    # Auto-resolve by tool binding
    registry = get_connections_registry()
    if not registry.exists():
        raise ConnectionError(
            "No connections configured.\n"
            f"Create a connection with: cc-browser connections add spotify --tool {TOOL_NAME}"
        )

    try:
        connections = json.loads(registry.read_text())
    except (json.JSONDecodeError, IOError) as e:
        raise ConnectionError(f"Cannot read connections registry: {e}")

    # Find connection bound to this tool
    for conn in connections:
        if conn.get("toolBinding") == TOOL_NAME:
            return conn["name"]

    # Not found - list available
    available = [c.get("name", "?") for c in connections]
    available_str = ", ".join(available) if available else "(none)"
    raise ConnectionError(
        f"No connection bound to tool '{TOOL_NAME}'.\n\n"
        f"Available connections: {available_str}\n\n"
        f"Create one with: cc-browser connections add spotify --tool {TOOL_NAME}\n"
        f"Or bind an existing one by editing connections.json and setting toolBinding to '{TOOL_NAME}'."
    )


def get_daemon_port() -> int:
    """Get daemon port from lockfile.

    Returns:
        Daemon port number (default 9280 if lockfile not found).
    """
    local_app_data = os.environ.get("LOCALAPPDATA", "")
    if not local_app_data:
        return DEFAULT_DAEMON_PORT

    lockfile = Path(local_app_data) / "cc-browser" / "daemon.lock"
    if lockfile.exists():
        try:
            data = json.loads(lockfile.read_text())
            return data.get("port", DEFAULT_DAEMON_PORT)
        except (json.JSONDecodeError, IOError):
            pass

    return DEFAULT_DAEMON_PORT


class ConnectionLockedError(BrowserError):
    """Raised when a connection is locked by another tool."""
    pass


class BrowserClient:
    """HTTP client for cc-browser v2 daemon.

    Communicates with the cc-browser daemon on localhost.
    Connection name is included in all POST requests.
    Acquires an exclusive lock on the connection to prevent concurrent use.
    """

    def __init__(self, workspace: str = None, connection: str = None,
                 profile: str = None, timeout: float = 30.0):
        """Initialize browser client for a connection.

        Args:
            workspace: Deprecated alias for connection (backward compat)
            connection: Connection name (e.g., "spotify")
            profile: Deprecated alias for connection
            timeout: HTTP request timeout in seconds

        Raises:
            ConnectionError: If connection cannot be resolved.
            ConnectionLockedError: If connection is locked by another tool.
        """
        explicit_name = connection or workspace or profile
        self.connection = resolve_connection(explicit_name)
        self.port = get_daemon_port()
        self.base_url = f"http://localhost:{self.port}"
        self.timeout = timeout
        self._client = httpx.Client(timeout=timeout)
        self._lock_acquired = False

        # Keep workspace as alias for backward compat
        self.workspace = self.connection

        # Acquire exclusive lock on the connection
        self._acquire_lock()

    def _acquire_lock(self):
        """Acquire exclusive lock on the connection via daemon."""
        try:
            response = self._client.post(
                f"{self.base_url}/connections/acquire",
                json={"name": self.connection, "owner": TOOL_NAME, "ttl": 300000}
            )
            result = response.json()
            if response.status_code == 409:
                raise ConnectionLockedError(
                    result.get("error", f"Connection '{self.connection}' is in use by another tool.")
                )
            if result.get("success"):
                self._lock_acquired = True
        except httpx.ConnectError:
            # Daemon not running - no locking available, proceed without lock
            pass

    def _release_lock(self):
        """Release exclusive lock on the connection."""
        if not self._lock_acquired:
            return
        try:
            self._client.post(
                f"{self.base_url}/connections/release",
                json={"name": self.connection, "owner": TOOL_NAME}
            )
            self._lock_acquired = False
        except (httpx.ConnectError, httpx.TimeoutException):
            pass

    def _renew_lock(self):
        """Renew the lock TTL (call periodically for long operations)."""
        if not self._lock_acquired:
            return
        try:
            self._client.post(
                f"{self.base_url}/connections/renew",
                json={"name": self.connection, "owner": TOOL_NAME, "ttl": 300000}
            )
        except (httpx.ConnectError, httpx.TimeoutException):
            pass

    def _post(self, endpoint: str, data: Optional[dict] = None) -> dict:
        """Send POST request to daemon with connection in body."""
        body = data.copy() if data else {}
        body["connection"] = self.connection
        try:
            response = self._client.post(
                f"{self.base_url}{endpoint}",
                json=body
            )
            result = response.json()

            if response.status_code == 409:
                raise ConnectionLockedError(
                    result.get("error", f"Connection '{self.connection}' is in use by another tool.")
                )

            if not result.get("success", False):
                raise BrowserError(result.get("error", "Unknown error"))

            return result
        except httpx.ConnectError:
            raise BrowserError(
                f"Cannot connect to cc-browser daemon on port {self.port}.\n"
                f"Start it with: cc-browser daemon"
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
                f"Start it with: cc-browser daemon"
            )
        except httpx.TimeoutException:
            raise BrowserError(f"Request timed out after {self.timeout}s")

    def status(self) -> dict:
        """Get daemon and browser status."""
        return self._get("/")

    def open_connection(self) -> dict:
        """Open browser for this connection."""
        return self._post("/connections/open", {"name": self.connection})

    def close_connection(self) -> dict:
        """Close browser for this connection."""
        return self._post("/connections/close", {"name": self.connection})

    def navigate(self, url: str) -> dict:
        """Navigate to URL."""
        return self._post("/navigate", {"url": url})

    def snapshot(self, interactive: bool = True) -> dict:
        """Get page snapshot with element refs."""
        return self._post("/snapshot", {"interactive": interactive})

    def info(self) -> dict:
        """Get current page info (URL, title)."""
        return self._post("/info")

    def text(self, selector: Optional[str] = None) -> dict:
        """Get page text content."""
        data = {}
        if selector:
            data["selector"] = selector
        return self._post("/text", data)

    def html(self, selector: Optional[str] = None) -> dict:
        """Get page HTML."""
        data = {}
        if selector:
            data["selector"] = selector
        return self._post("/html", data)

    def click(self, ref: str) -> dict:
        """Click element by ref."""
        return self._post("/click", {"ref": ref})

    def type(self, ref: str, text: str) -> dict:
        """Type text into element."""
        return self._post("/type", {"ref": ref, "text": text})

    def press(self, key: str) -> dict:
        """Press keyboard key."""
        return self._post("/press", {"key": key})

    def hover(self, ref: str) -> dict:
        """Hover over element."""
        return self._post("/hover", {"ref": ref})

    def select(self, ref: str, value: str) -> dict:
        """Select dropdown option."""
        return self._post("/select", {"ref": ref, "value": value})

    def scroll(self, direction: str = "down", ref: Optional[str] = None,
               amount: Optional[int] = None) -> dict:
        """Scroll page or element.

        Args:
            direction: "up", "down", "left", "right"
            ref: Element ref to scroll into view (overrides direction scroll)
            amount: Pixels to scroll (default 500 in daemon).
        """
        data = {"direction": direction}
        if ref:
            data["ref"] = ref
        if amount is not None:
            data["amount"] = amount
        return self._post("/scroll", data)

    def screenshot(self, full_page: bool = False) -> dict:
        """Take screenshot (returns base64)."""
        return self._post("/screenshot", {"fullPage": full_page})

    def wait_for_text(self, text: str, timeout: int = 5000) -> dict:
        """Wait for text to appear."""
        return self._post("/wait", {"text": text, "timeout": timeout})

    def wait(self, ms: int) -> dict:
        """Wait for specified time."""
        return self._post("/wait", {"time": ms})

    def evaluate(self, js: str) -> dict:
        """Execute JavaScript."""
        return self._post("/evaluate", {"js": js})

    def fill(self, fields: list) -> dict:
        """Fill multiple form fields."""
        return self._post("/fill", {"fields": fields})

    def upload(self, ref: str, path: str) -> dict:
        """Upload file."""
        return self._post("/upload", {"ref": ref, "path": path})

    def tabs(self) -> dict:
        """List all tabs."""
        return self._post("/tabs")

    def tabs_open(self, url: Optional[str] = None) -> dict:
        """Open new tab."""
        data = {}
        if url:
            data["url"] = url
        return self._post("/tabs/open", data)

    def tabs_close(self, tab_id: str) -> dict:
        """Close tab."""
        return self._post("/tabs/close", {"tab": tab_id})

    def tabs_focus(self, tab_id: str) -> dict:
        """Focus tab."""
        return self._post("/tabs/focus", {"tab": tab_id})

    def close(self):
        """Release lock and close HTTP client."""
        self._release_lock()
        self._client.close()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


# Convenience function for quick operations
def get_client(workspace: str = None, connection: str = None) -> BrowserClient:
    """Get a browser client instance for a connection."""
    return BrowserClient(connection=connection or workspace)
