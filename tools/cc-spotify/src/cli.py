"""cc-spotify CLI - Spotify control via browser automation."""

import typer
from rich.console import Console
from rich.table import Table
from typing import Optional
import json
from pathlib import Path

from .browser_client import BrowserClient, BrowserError, WorkspaceError
from .selectors import SpotifyKeys, SpotifySelectors, SpotifyURLs
from .delays import jittered_sleep
from .spotify_js import (
    get_now_playing_js,
    get_shuffle_state_js,
    get_repeat_state_js,
    get_playlists_js,
    get_search_results_js,
    get_queue_js,
    set_volume_js,
)

app = typer.Typer(
    name="cc-spotify",
    help="Spotify CLI via browser automation",
    no_args_is_help=True,
)

console = Console()


# =============================================================================
# Config Helpers
# =============================================================================

def get_config_dir() -> Path:
    """Get cc-spotify config directory."""
    try:
        from cc_storage import CcStorage
        return CcStorage.tool_config("spotify")
    except ImportError:
        import sys
        _tools_dir = str(Path(__file__).resolve().parent.parent.parent)
        if _tools_dir not in sys.path:
            sys.path.insert(0, _tools_dir)
        from cc_storage import CcStorage
        return CcStorage.tool_config("spotify")


def load_default_workspace() -> Optional[str]:
    """Load default workspace from config.json.

    Returns:
        Default workspace name from config, or None if not configured.
    """
    config_file = get_config_dir() / "config.json"

    if not config_file.exists():
        return None

    try:
        with open(config_file, "r") as f:
            data = json.load(f)
        return data.get("default_workspace")
    except json.JSONDecodeError as e:
        console.print(f"[red]ERROR:[/red] Invalid JSON in {config_file}: {e}")
        raise typer.Exit(1)
    except IOError as e:
        console.print(f"[red]ERROR:[/red] Cannot read {config_file}: {e}")
        raise typer.Exit(1)


def save_config(workspace: str) -> Path:
    """Save workspace to config.json."""
    config_dir = get_config_dir()
    config_dir.mkdir(parents=True, exist_ok=True)
    config_file = config_dir / "config.json"

    data = {}
    if config_file.exists():
        try:
            with open(config_file, "r") as f:
                data = json.load(f)
        except (json.JSONDecodeError, IOError):
            data = {}

    data["default_workspace"] = workspace

    with open(config_file, "w") as f:
        json.dump(data, f, indent=2)

    return config_file


def list_available_workspaces() -> list[str]:
    """List available cc-browser workspaces."""
    from .browser_client import get_cc_browser_dir
    cc_browser_dir = get_cc_browser_dir()
    workspaces = []
    if cc_browser_dir.exists():
        for d in cc_browser_dir.iterdir():
            if d.is_dir() and (d / "workspace.json").exists():
                workspaces.append(d.name)
    return workspaces


# =============================================================================
# Global Options
# =============================================================================

class AppConfig:
    workspace: str = ""
    format: str = "text"
    verbose: bool = False


app_config = AppConfig()


def get_client() -> BrowserClient:
    """Get browser client instance for configured workspace."""
    try:
        return BrowserClient(workspace=app_config.workspace)
    except WorkspaceError as e:
        console.print(f"[red]ERROR:[/red] {e}")
        raise typer.Exit(1)


def error(msg: str):
    """Print error message."""
    console.print(f"[red]ERROR:[/red] {msg}")


def success(msg: str):
    """Print success message."""
    console.print(f"[green]OK:[/green] {msg}")


def output_json(data: dict):
    """Output data in configured format."""
    if app_config.format == "json":
        console.print_json(json.dumps(data))
    else:
        return data


def verbose_snapshot(client: BrowserClient):
    """If verbose, dump the current page snapshot."""
    if app_config.verbose:
        try:
            snap = client.snapshot()
            console.print("[dim]--- Snapshot ---[/dim]")
            console.print_json(json.dumps(snap, indent=2)[:3000])
            console.print("[dim]--- End Snapshot ---[/dim]")
        except BrowserError:
            console.print("[dim]Could not get snapshot[/dim]")


def parse_js_result(result: dict) -> dict:
    """Parse JavaScript evaluation result from cc-browser."""
    raw = result.get("result", result.get("value", "{}"))
    if isinstance(raw, str):
        try:
            return json.loads(raw)
        except json.JSONDecodeError:
            return {"raw": raw}
    if isinstance(raw, dict):
        return raw
    return {"raw": str(raw)}


@app.callback()
def main(
    workspace: Optional[str] = typer.Option(
        None, "--workspace", "-w",
        help="cc-browser workspace name or alias"
    ),
    format: str = typer.Option(
        "text", "--format", "-f",
        help="Output format: text, json"
    ),
    verbose: bool = typer.Option(
        False, "--verbose", "-v",
        help="Verbose output (dump snapshots for debugging)"
    ),
):
    """Spotify CLI via browser automation.

    Control Spotify Web Player through a cc-browser workspace.
    Requires cc-browser daemon to be running with Spotify open.

    First-time setup:
      cc-spotify config --workspace <name>
      cc-browser start --workspace <name>
      (navigate to open.spotify.com and log in)
    """
    if workspace is None:
        workspace = load_default_workspace()

    if workspace is None:
        # Only fail if running a command that needs a workspace
        # (not 'config')
        workspace = ""

    app_config.workspace = workspace
    app_config.format = format
    app_config.verbose = verbose


def _ensure_workspace():
    """Ensure workspace is configured before running commands."""
    if not app_config.workspace:
        console.print(
            "[red]ERROR:[/red] No workspace configured.\n\n"
            "Use 'cc-spotify config --workspace <name>' to set one.\n"
        )
        available = list_available_workspaces()
        if available:
            console.print("Available workspaces:")
            for ws in available:
                console.print(f"  - {ws}")
        raise typer.Exit(1)


# =============================================================================
# Config Command
# =============================================================================

@app.command()
def config(
    workspace: Optional[str] = typer.Option(
        None, "--workspace", "-w",
        help="Set default workspace"
    ),
    show: bool = typer.Option(
        False, "--show", "-s",
        help="Show current config"
    ),
):
    """Configure cc-spotify settings."""
    config_file = get_config_dir() / "config.json"

    if show or workspace is None:
        if config_file.exists():
            with open(config_file, "r") as f:
                data = json.load(f)
            console.print(f"Config file: {config_file}")
            console.print_json(json.dumps(data, indent=2))
        else:
            console.print(f"No config file found at: {config_file}")
            console.print("Run: cc-spotify config --workspace <name>")

        available = list_available_workspaces()
        if available:
            console.print("\nAvailable cc-browser workspaces:")
            for ws in available:
                console.print(f"  - {ws}")
        return

    saved = save_config(workspace)
    success(f"Default workspace set to '{workspace}'")
    console.print(f"Config saved to: {saved}")


# =============================================================================
# Status Command
# =============================================================================

@app.command()
def status():
    """Check cc-browser daemon and Spotify connection status."""
    _ensure_workspace()

    try:
        client = get_client()

        result = client.status()
        console.print(f"[green]cc-browser daemon:[/green] running (port {client.port})")

        browser_status = result.get("browser", "unknown")
        console.print(f"[green]Browser:[/green] {browser_status}")

        try:
            info = client.info()
            url = info.get("url", "")
            if "open.spotify.com" in url:
                console.print(f"[green]Spotify:[/green] connected ({url})")

                # Try to get now playing info
                js_result = client.evaluate(get_now_playing_js())
                track_data = parse_js_result(js_result)
                if track_data.get("name"):
                    state = "Playing" if track_data.get("is_playing") else "Paused"
                    console.print(
                        f"[green]Now:[/green] {track_data['name']} - "
                        f"{track_data.get('artist', '?')} [{state}]"
                    )
            else:
                console.print(
                    f"[yellow]Current page:[/yellow] {url}\n"
                    "Navigate to open.spotify.com to use cc-spotify."
                )
        except BrowserError:
            console.print("[yellow]Browser:[/yellow] no page loaded")

        verbose_snapshot(client)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Now Playing
# =============================================================================

@app.command()
def now():
    """Show currently playing track info."""
    _ensure_workspace()

    try:
        client = get_client()
        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)

        if data.get("error"):
            error(data["error"])
            console.print("Make sure Spotify Web Player is open and a track is loaded.")
            raise typer.Exit(1)

        if app_config.format == "json":
            console.print_json(json.dumps(data))
            return

        state = "Playing" if data.get("is_playing") else "Paused"
        liked = " [red]*[/red]" if data.get("is_liked") else ""

        console.print(f"[bold]{data.get('name', '?')}[/bold]{liked}")
        console.print(f"  Artist: {data.get('artist', '?')}")
        pos = data.get("position", "")
        dur = data.get("duration", "")
        if pos and dur:
            console.print(f"  Time:   {pos} / {dur}")
        console.print(f"  Status: {state}")

        verbose_snapshot(client)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Playback Controls
# =============================================================================

@app.command()
def play():
    """Resume playback."""
    _ensure_workspace()

    try:
        client = get_client()

        # Check if already playing
        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)
        if data.get("is_playing"):
            console.print("Already playing.")
            return

        client.press(SpotifyKeys.PLAY_PAUSE)
        jittered_sleep(0.5)
        success("Playback resumed")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def pause():
    """Pause playback."""
    _ensure_workspace()

    try:
        client = get_client()

        # Check if already paused
        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)
        if not data.get("is_playing"):
            console.print("Already paused.")
            return

        client.press(SpotifyKeys.PLAY_PAUSE)
        jittered_sleep(0.5)
        success("Playback paused")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command(name="next")
def next_track():
    """Skip to next track."""
    _ensure_workspace()

    try:
        client = get_client()
        client.press(SpotifyKeys.NEXT_TRACK)
        jittered_sleep(1.0)

        # Show what's now playing
        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)
        if data.get("name"):
            console.print(f"Now playing: {data['name']} - {data.get('artist', '?')}")
        else:
            success("Skipped to next track")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command(name="prev")
def prev_track():
    """Go to previous track."""
    _ensure_workspace()

    try:
        client = get_client()
        client.press(SpotifyKeys.PREV_TRACK)
        jittered_sleep(1.0)

        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)
        if data.get("name"):
            console.print(f"Now playing: {data['name']} - {data.get('artist', '?')}")
        else:
            success("Went to previous track")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Shuffle / Repeat / Volume / Like
# =============================================================================

@app.command()
def shuffle(
    on: bool = typer.Option(False, "--on", help="Turn shuffle on"),
    off: bool = typer.Option(False, "--off", help="Turn shuffle off"),
):
    """Toggle shuffle mode, or set explicitly with --on/--off."""
    _ensure_workspace()

    try:
        client = get_client()

        if on or off:
            # Check current state first
            js_result = client.evaluate(get_shuffle_state_js())
            data = parse_js_result(js_result)
            current = data.get("shuffle", False)

            if (on and current) or (off and not current):
                state_str = "on" if current else "off"
                console.print(f"Shuffle is already {state_str}.")
                return

        # Use snapshot + click for shuffle button
        snap = client.snapshot()
        verbose_snapshot(client)
        _click_by_testid(client, snap, "control-button-shuffle")
        jittered_sleep(0.5)

        # Report new state
        js_result = client.evaluate(get_shuffle_state_js())
        data = parse_js_result(js_result)
        state_str = "on" if data.get("shuffle") else "off"
        success(f"Shuffle: {state_str}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def repeat(
    mode: Optional[str] = typer.Argument(
        None,
        help="Target mode: off, context, track (omit to cycle)"
    ),
):
    """Set repeat mode or cycle through modes."""
    _ensure_workspace()

    try:
        client = get_client()

        if mode and mode not in ("off", "context", "track"):
            error("Mode must be one of: off, context, track")
            raise typer.Exit(1)

        # Click the repeat button (cycles: off -> context -> track -> off)
        snap = client.snapshot()
        verbose_snapshot(client)
        _click_by_testid(client, snap, "control-button-repeat")
        jittered_sleep(0.5)

        # If a specific mode is requested, keep clicking until we reach it
        if mode:
            for _ in range(3):
                js_result = client.evaluate(get_repeat_state_js())
                data = parse_js_result(js_result)
                if data.get("repeat") == mode:
                    break
                snap = client.snapshot()
                _click_by_testid(client, snap, "control-button-repeat")
                jittered_sleep(0.5)

        # Report state
        js_result = client.evaluate(get_repeat_state_js())
        data = parse_js_result(js_result)
        success(f"Repeat: {data.get('repeat', 'unknown')}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def volume(
    level: int = typer.Argument(..., help="Volume level 0-100"),
):
    """Set volume level (0-100)."""
    _ensure_workspace()

    if level < 0 or level > 100:
        error("Volume must be between 0 and 100")
        raise typer.Exit(1)

    try:
        client = get_client()
        js_result = client.evaluate(set_volume_js(level))
        data = parse_js_result(js_result)

        if data.get("error"):
            error(data["error"])
            console.print(
                "Volume slider may not be accessible. "
                "Try clicking in the Spotify window first."
            )
            raise typer.Exit(1)

        success(f"Volume set to {level}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def like():
    """Heart/save the currently playing track."""
    _ensure_workspace()

    try:
        client = get_client()

        # Try keyboard shortcut first (most reliable)
        client.press(SpotifyKeys.LIKE)
        jittered_sleep(0.5)

        # Check result
        js_result = client.evaluate(get_now_playing_js())
        data = parse_js_result(js_result)
        if data.get("is_liked"):
            success(f"Liked: {data.get('name', 'current track')}")
        else:
            console.print(
                "Toggled like state for: "
                f"{data.get('name', 'current track')}"
            )

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Search
# =============================================================================

@app.command()
def search(
    query: str = typer.Argument(..., help="Search query"),
):
    """Search Spotify for tracks, artists, albums."""
    _ensure_workspace()

    try:
        client = get_client()

        # Navigate to search URL
        url = SpotifyURLs.search(query)
        client.navigate(url)
        jittered_sleep(3.0)

        # Extract results via JS
        js_result = client.evaluate(get_search_results_js())
        results = parse_js_result(js_result)

        verbose_snapshot(client)

        if app_config.format == "json":
            console.print_json(json.dumps(results))
            return

        if isinstance(results, list):
            if not results:
                console.print(f"No results found for: {query}")
                return

            table = Table(title=f"Search: {query}")
            table.add_column("#", style="dim", width=4)
            table.add_column("Name", style="bold")
            table.add_column("Artist")
            table.add_column("Type", style="dim")

            for i, r in enumerate(results[:20], 1):
                table.add_row(
                    str(i),
                    r.get("name", ""),
                    r.get("artist", ""),
                    r.get("type", ""),
                )
            console.print(table)
        else:
            console.print("Unexpected result format.")
            if app_config.verbose:
                console.print_json(json.dumps(results))

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Playlists
# =============================================================================

@app.command()
def playlists():
    """List playlists from the sidebar."""
    _ensure_workspace()

    try:
        client = get_client()

        js_result = client.evaluate(get_playlists_js())
        data = parse_js_result(js_result)

        verbose_snapshot(client)

        if app_config.format == "json":
            console.print_json(json.dumps(data))
            return

        if isinstance(data, list):
            if not data:
                console.print("No playlists found in sidebar.")
                console.print("Make sure the sidebar is visible and playlists are loaded.")
                return

            table = Table(title="Your Playlists")
            table.add_column("#", style="dim", width=4)
            table.add_column("Name", style="bold")

            for i, p in enumerate(data, 1):
                table.add_row(str(i), p.get("name", ""))
            console.print(table)
        else:
            console.print("Unexpected result format.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def playlist(
    name: str = typer.Argument(..., help="Playlist name to play"),
):
    """Play a playlist by name (searches sidebar)."""
    _ensure_workspace()

    try:
        client = get_client()

        # Get playlists from sidebar
        js_result = client.evaluate(get_playlists_js())
        data = parse_js_result(js_result)

        if not isinstance(data, list) or not data:
            error("No playlists found in sidebar.")
            raise typer.Exit(1)

        # Find matching playlist (case-insensitive partial match)
        name_lower = name.lower()
        match = None
        for p in data:
            if name_lower in p.get("name", "").lower():
                match = p
                break

        if not match:
            error(f"Playlist '{name}' not found.")
            console.print("Available playlists:")
            for p in data[:10]:
                console.print(f"  - {p.get('name', '')}")
            raise typer.Exit(1)

        # Click the playlist via snapshot
        snap = client.snapshot()
        snapshot_text = json.dumps(snap)

        # Find the playlist element ref from the snapshot
        found_ref = _find_text_ref(snap, match["name"])
        if found_ref:
            client.click(found_ref)
            jittered_sleep(2.0)
            success(f"Opened playlist: {match['name']}")
        else:
            # Navigate to collection playlists and try to find it there
            console.print(f"Could not click playlist directly. Try: cc-spotify goto <playlist-url>")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Queue
# =============================================================================

@app.command()
def queue():
    """Show the playback queue."""
    _ensure_workspace()

    try:
        client = get_client()

        # Navigate to queue view
        client.navigate(SpotifyURLs.queue())
        jittered_sleep(2.0)

        js_result = client.evaluate(get_queue_js())
        data = parse_js_result(js_result)

        verbose_snapshot(client)

        if app_config.format == "json":
            console.print_json(json.dumps(data))
            return

        if isinstance(data, list):
            if not data:
                console.print("Queue is empty.")
                return

            table = Table(title="Playback Queue")
            table.add_column("#", style="dim", width=4)
            table.add_column("Track", style="bold")
            table.add_column("Artist")

            for track in data[:30]:
                table.add_row(
                    str(track.get("position", "")),
                    track.get("name", ""),
                    track.get("artist", ""),
                )
            console.print(table)

            if len(data) > 30:
                console.print(f"[dim]... and {len(data) - 30} more tracks[/dim]")
        else:
            console.print("Unexpected result format.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Navigate
# =============================================================================

@app.command()
def goto(
    url: str = typer.Argument(..., help="Spotify URL to navigate to"),
):
    """Navigate to a Spotify URL."""
    _ensure_workspace()

    if not url.startswith("http"):
        # Assume it's a Spotify URI or path
        if url.startswith("spotify:"):
            # Convert URI to URL: spotify:track:123 -> open.spotify.com/track/123
            parts = url.replace("spotify:", "").split(":")
            if len(parts) == 2:
                url = f"{SpotifyURLs.BASE}/{parts[0]}/{parts[1]}"
            else:
                error(f"Cannot parse Spotify URI: {url}")
                raise typer.Exit(1)
        elif url.startswith("/"):
            url = f"{SpotifyURLs.BASE}{url}"
        else:
            url = f"{SpotifyURLs.BASE}/{url}"

    try:
        client = get_client()
        client.navigate(url)
        jittered_sleep(2.0)
        success(f"Navigated to: {url}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Recommend (Vault Integration)
# =============================================================================

@app.command()
def recommend(
    mood: Optional[str] = typer.Option(
        None, "--mood", "-m",
        help="Mood or genre hint (e.g., 'chill jazz', 'energetic workout')"
    ),
):
    """Get music recommendations powered by vault preferences."""
    _ensure_workspace()

    try:
        from .vault_integration import get_recommendations
        suggestions = get_recommendations(mood=mood)

        if not suggestions:
            console.print(
                "No music preferences found in vault.\n\n"
                "Add preferences with:\n"
                "  cc-vault docs import music-preferences.md\n\n"
                "Example content:\n"
                "  # Music Preferences\n"
                "  - Genres: jazz, ambient, indie rock\n"
                "  - Artists: Miles Davis, Radiohead, Khruangbin\n"
                "  - Moods: chill for work, energetic for workouts"
            )
            return

        console.print("[bold]Vault Recommendations:[/bold]")
        for s in suggestions:
            console.print(f"  -> {s}")

        # Offer to search the first suggestion
        if suggestions:
            console.print(
                f"\nSearch Spotify: cc-spotify search \"{suggestions[0]}\""
            )

    except ImportError:
        error("vault_integration module not available")
        raise typer.Exit(1)
    except Exception as e:
        error(f"Vault query failed: {e}")
        console.print("Make sure cc-vault is installed and accessible.")
        raise typer.Exit(1)


# =============================================================================
# Snapshot Helpers
# =============================================================================

def _click_by_testid(client: BrowserClient, snapshot: dict, testid: str):
    """Find and click an element by data-testid in a snapshot.

    Searches the snapshot tree for an element with matching data-testid
    and clicks it via its ref.
    """
    ref = _find_testid_ref(snapshot, testid)
    if ref:
        client.click(ref)
        return True

    # Fallback: try CSS selector click
    error(f"Element with data-testid='{testid}' not found in snapshot")
    return False


def _find_testid_ref(snapshot: dict, testid: str) -> Optional[str]:
    """Recursively search snapshot for element with data-testid."""
    return _search_snapshot(
        snapshot,
        lambda node: testid in node.get("attributes", {}).get("data-testid", "")
    )


def _find_text_ref(snapshot: dict, text: str) -> Optional[str]:
    """Recursively search snapshot for element containing text."""
    text_lower = text.lower()
    return _search_snapshot(
        snapshot,
        lambda node: text_lower in (node.get("text", "") or "").lower()
    )


def _search_snapshot(data: dict, predicate) -> Optional[str]:
    """Search snapshot tree for a node matching predicate. Returns ref."""
    if isinstance(data, dict):
        if predicate(data) and data.get("ref"):
            return data["ref"]

        # Search children
        for key in ("children", "nodes", "content"):
            children = data.get(key, [])
            if isinstance(children, list):
                for child in children:
                    result = _search_snapshot(child, predicate)
                    if result:
                        return result
            elif isinstance(children, dict):
                result = _search_snapshot(children, predicate)
                if result:
                    return result
    elif isinstance(data, list):
        for item in data:
            result = _search_snapshot(item, predicate)
            if result:
                return result

    return None
