"""The credential the command line presents to its own Director's Control API.

The Director accepts ONE machine secret and any scoped token derived from it. The derivation is a
signature over the scope name and, for a session-bound credential, the session id:

    v1.<scope>.<sessionId-or-empty>.<base64url HMAC-SHA256(root, "<scope>\\n<sessionId>")>

The command line derives the "cli" scope. That is full authority today - on a single-user desktop
anyone who can derive it could have read the root secret instead - but it is a DISTINCT value, so the
raw machine secret never travels on the wire, and the two can be told apart later without reissuing
anything.

Resolving the ROOT secret has to match the Director exactly or every call is answered 401. The
Director accepts the shared fleet token when this machine is attached to a Gateway, and its own
persisted token otherwise (DirectorAuth.ResolveAcceptedToken). Reading only the token file - which is
what this did while authentication was switched off and nothing noticed - is correct on a standalone
machine and wrong on every machine with a Gateway configured.

WHERE those files live is the second half of the same problem. Every Director - the default
included - keeps its whole storage under its own instance home, <shared-root>/instances/<slug>
(InstanceContext.cs), so on a clean install the machine-wide root's own config directory is EMPTY
and reading it finds no secret at all. The secret that verifies a call is the one belonging to the
Director actually being called, so the root is resolved per instance: the CC_DIRECTOR_ROOT a
Director stamps into its own sessions wins outright; an ordinary shell matches the endpoint it
targets (CC_DIRECTOR_API) against the live instance registrations and uses the matched Director's
home; with nothing to match, the default instance's home is used when it exists, and the flat
machine root only for a pre-instance install that never had homes.
"""

import base64
import hashlib
import hmac
import json
import os
import sys
from pathlib import Path
from typing import Iterator, Optional, Tuple
from urllib.parse import urlsplit

SCOPE_CLI = "cli"
SCOPE_ADMIN = "admin"
SCOPE_SESSION_CHILD = "session-child"


def _machine_shared_root() -> Path:
    """The machine-wide cc-director root - the parent of every instance home, never an instance."""
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA", "")
        if not base:
            raise RuntimeError("LOCALAPPDATA is not set; cannot locate the cc-director storage root.")
        return Path(base) / "cc-director"
    # macOS and Linux: .NET maps LocalApplicationData to ~/.local/share
    return Path(os.path.expanduser("~")) / ".local" / "share" / "cc-director"


def _pid_alive(pid) -> bool:
    """True if a process with this pid exists. Dead Directors leave their registration files behind."""
    if not isinstance(pid, int) or pid <= 0:
        return False
    if sys.platform == "win32":
        import ctypes
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        handle = ctypes.windll.kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not handle:
            return False
        ctypes.windll.kernel32.CloseHandle(handle)
        return True
    try:
        os.kill(pid, 0)
        return True
    except (OSError, ProcessLookupError):
        return False


def _ci_get(obj: dict, key: str):
    """Case-insensitive key lookup (registration files are PascalCase today)."""
    for k, v in obj.items():
        if k.lower() == key.lower():
            return v
    return None


def _registration_files(shared: Path) -> Iterator[Tuple[Path, Path]]:
    """Every (instance home, registration file) pair on this machine.

    A Director registers under its OWN home: <home>/config/director/instances/<id>.json. From 1.8
    every home is <shared>/instances/<slug>; the flat shared root is scanned too, for a
    pre-instance Director.
    """
    homes = [shared]
    instances = shared / "instances"
    try:
        if instances.is_dir():
            homes.extend(p for p in sorted(instances.iterdir()) if p.is_dir())
    except OSError:
        pass
    for home in homes:
        reg_dir = home / "config" / "director" / "instances"
        try:
            if not reg_dir.is_dir():
                continue
            for f in sorted(reg_dir.glob("*.json")):
                yield home, f
        except OSError:
            continue


def _home_matching_endpoint(shared: Path) -> Optional[Path]:
    """The home of the LIVE Director registered on the endpoint this process targets, or None.

    The endpoint is CC_DIRECTOR_API - the Director this credential will be presented to. Matching
    its port against the live registrations is what makes "the secret read" and "the Director
    called" the same instance when several run side by side.
    """
    url = os.environ.get("CC_DIRECTOR_API", "").strip()
    if not url:
        return None
    try:
        target_port = urlsplit(url).port
    except ValueError:
        return None
    if target_port is None:
        return None

    matches = []  # (started_at, home) - newest StartedAt wins if a stale twin shares the port
    for home, f in _registration_files(shared):
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        if not isinstance(data, dict):
            continue
        endpoint = _ci_get(data, "ControlEndpoint")
        if not isinstance(endpoint, str):
            continue
        try:
            port = urlsplit(endpoint).port
        except ValueError:
            continue
        if port != target_port:
            continue
        if not _pid_alive(_ci_get(data, "Pid")):
            continue
        started = _ci_get(data, "StartedAt")
        matches.append((started if isinstance(started, str) else "", home))

    if not matches:
        return None
    matches.sort(key=lambda m: m[0], reverse=True)  # ISO-8601 sorts lexically
    return matches[0][1]


def storage_root() -> Path:
    """The storage root of the Director this process is talking to.

    Resolution order, most specific first:
      1. CC_DIRECTOR_ROOT - a Director stamps its own instance home into every session it spawns
         (and tests pin it); inside a session this IS the right Director's home.
      2. The instance whose live registration answers on the CC_DIRECTOR_API endpoint - an ordinary
         shell aiming at a specific Director reads that Director's secret, not a neighbour's.
      3. <shared>/instances/default when it exists - the clean 1.8+ install, where even the default
         Director's storage lives one level in and the flat root's config directory is empty.
      4. The flat machine root - only a pre-instance install still keeps its secret there.
    """
    override = os.environ.get("CC_DIRECTOR_ROOT")
    if override:
        return Path(override)

    shared = _machine_shared_root()
    matched = _home_matching_endpoint(shared)
    if matched is not None:
        return matched

    default_home = shared / "instances" / "default"
    if default_home.is_dir():
        return default_home
    return shared


def config_json_path() -> Path:
    """config.json, which carries gateway.token when this machine is attached to a Gateway."""
    return storage_root() / "config" / "config.json"


def token_file_path() -> Path:
    """The Director's own persisted machine secret, used when no Gateway is configured."""
    return storage_root() / "config" / "director" / "gateway-token.txt"


def root_secret() -> Optional[str]:
    """The secret this machine's Director accepts, or None when neither source is readable.

    Same order as the Director: the shared fleet token from config.json when one is configured,
    otherwise the Director's own persisted token.
    """
    try:
        path = config_json_path()
        if path.is_file():
            config = json.loads(path.read_text(encoding="utf-8"))
            if isinstance(config, dict):
                gateway = config.get("gateway")
                if isinstance(gateway, dict):
                    token = gateway.get("token")
                    if isinstance(token, str) and token.strip():
                        return token.strip()
    except (OSError, ValueError):
        # An unreadable or malformed config.json is not the answer to "what is the secret" - fall
        # through to the token file, which is where a standalone Director keeps it.
        pass

    try:
        path = token_file_path()
        if path.is_file():
            value = path.read_text(encoding="utf-8").strip()
            return value or None
    except OSError:
        return None
    return None


def mint(root: str, scope: str, session_id: Optional[str] = None) -> str:
    """Derive a scoped token from the root secret."""
    bound = session_id or ""
    if scope == SCOPE_SESSION_CHILD and not bound:
        raise ValueError("a session-child token must be bound to a session id")
    if scope != SCOPE_SESSION_CHILD and bound:
        raise ValueError(f"scope '{scope}' is not session-bound; pass no session id")

    mac = hmac.new(root.encode("utf-8"), f"{scope}\n{bound}".encode("utf-8"), hashlib.sha256).digest()
    signature = base64.urlsafe_b64encode(mac).decode("ascii").rstrip("=")
    return f"v1.{scope}.{bound}.{signature}"


def cli_token() -> Optional[str]:
    """The command line's own credential, or None when the machine secret cannot be read."""
    root = root_secret()
    if not root:
        return None
    return mint(root, SCOPE_CLI)
