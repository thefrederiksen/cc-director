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
"""

import base64
import hashlib
import hmac
import json
import os
import sys
from pathlib import Path
from typing import Optional

SCOPE_CLI = "cli"
SCOPE_ADMIN = "admin"
SCOPE_SESSION_CHILD = "session-child"


def storage_root() -> Path:
    """The cc-director storage root, resolved the way the application resolves it."""
    override = os.environ.get("CC_DIRECTOR_ROOT")
    if override:
        return Path(override)
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA", "")
        if not base:
            raise RuntimeError("LOCALAPPDATA is not set; cannot locate the cc-director storage root.")
        return Path(base) / "cc-director"
    # macOS and Linux: .NET maps LocalApplicationData to ~/.local/share
    return Path(os.path.expanduser("~")) / ".local" / "share" / "cc-director"


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
