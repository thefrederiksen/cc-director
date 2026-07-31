#!/usr/bin/env python3
"""Read and write CC Director settings via a running Director's Control API.

Discovers a running Director from its instance registration files, then talks to the
loopback Control API:

    GET /settings        -> the whole config.json as JSON
    PUT /settings <obj>  -> deep-merges a partial patch into config.json (siblings preserved)

Because writes go through the running Director, gateway changes are applied live (the
Director re-registers with the gateway) - no app restart needed.

Usage:
    python configure_settings.py show
    python configure_settings.py get screenshots.source_directory
    python configure_settings.py set-screenshots "/Users/you/Desktop"
    python configure_settings.py set-gateway --url http://gw-host:7878 \
        --advertised http://this-host:7879 [--token TOKEN]
    python configure_settings.py set <dotted.key> <value>

Every route but /healthz requires a credential. This script reads the Director's secret itself,
from the home of the INSTANCE it discovered - every Director, the default included, keeps its
storage under <base>/instances/<slug> - preferring gateway.token from that home's config/config.json
when the machine is attached to a Gateway, otherwise that home's config/director/gateway-token.txt.
No token normally has to be passed. Use --director-token to override it.

ASCII-only output. No Unicode.
"""

import argparse
import json
import os
import sys
import urllib.request
import urllib.error
from pathlib import Path


def _local_app_data() -> Path:
    """Resolve the cc-director storage base, honoring CC_DIRECTOR_ROOT like the app does."""
    override = os.environ.get("CC_DIRECTOR_ROOT")
    if override:
        return Path(override)
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA", "")
        if not base:
            raise RuntimeError("LOCALAPPDATA is not set; cannot locate cc-director config.")
        return Path(base) / "cc-director"
    # macOS/Linux: .NET maps LocalApplicationData to ~/.local/share
    return Path(os.path.expanduser("~")) / ".local" / "share" / "cc-director"


def _instance_homes() -> list[Path]:
    """Every storage home a Director on this machine may run from.

    Every Director - the default included - keeps its whole storage under its own home,
    <base>/instances/<slug>, and registers THERE; the flat base itself is included for a
    pre-instance install. The registration and the secret live under the same home, which is
    what lets discovery hand back a credential that the discovered Director actually accepts.
    """
    base = _local_app_data()
    homes = [base]
    instances = base / "instances"
    try:
        if instances.is_dir():
            homes.extend(p for p in sorted(instances.iterdir()) if p.is_dir())
    except OSError:
        pass
    return homes


def _token_file(home: Path) -> Path:
    return home / "config" / "director" / "gateway-token.txt"


def _pid_alive(pid: int) -> bool:
    """True if a process with this pid exists."""
    if pid <= 0:
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
    """Case-insensitive key lookup (instance files are PascalCase today)."""
    for k, v in obj.items():
        if k.lower() == key.lower():
            return v
    return None


def discover_director() -> tuple[str, Path]:
    """Find the newest LIVE Director: its loopback control endpoint AND the instance home it runs
    from, e.g. ("http://127.0.0.1:7883", .../cc-director/instances/default).

    The home matters as much as the endpoint: the secret that authorizes calls to that endpoint is
    stored under that home. Reading registrations from only the flat base - which is what this did -
    found no Director at all on a clean install, where even the default instance registers one
    level in.

    Raises with a clear message if none is running.
    """
    candidates = []
    searched = []
    for home in _instance_homes():
        d = home / "config" / "director" / "instances"
        searched.append(str(d))
        if not d.is_dir():
            continue
        for f in d.glob("*.json"):
            try:
                data = json.loads(f.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                continue
            endpoint = _ci_get(data, "ControlEndpoint")
            pid = _ci_get(data, "Pid")
            started = _ci_get(data, "StartedAt") or ""
            if not endpoint or not isinstance(pid, int):
                continue
            if not _pid_alive(pid):
                continue
            candidates.append((started, endpoint, home))

    if not candidates:
        raise RuntimeError(
            "Found no running Director (searched " + ", ".join(searched)
            + "). Start CC Director, then retry."
        )

    # Newest by StartedAt (ISO-8601 sorts lexically).
    candidates.sort(key=lambda c: c[0], reverse=True)
    return candidates[0][1].rstrip("/"), candidates[0][2]


def _request(method: str, url: str, token: str | None, body: dict | None = None,
             token_file_hint: Path | None = None) -> dict:
    data = json.dumps(body).encode("utf-8") if body is not None else None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            text = resp.read().decode("utf-8")
            return json.loads(text) if text.strip() else {}
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", errors="replace")
        if e.code == 401:
            hint = f" or set the token at {token_file_hint}" if token_file_hint else ""
            raise RuntimeError(
                f"401 Unauthorized: this Director refused the credential. Pass --director-token{hint}."
            ) from e
        raise RuntimeError(f"{method} {url} failed: HTTP {e.code} {detail}") from e
    except urllib.error.URLError as e:
        raise RuntimeError(f"{method} {url} failed: {e.reason}") from e


def _config_json(home: Path) -> Path:
    return home / "config" / "config.json"


def _resolve_token(explicit: str | None, home: Path) -> str | None:
    """The secret of the Director INSTANCE being called, read from that instance's home.

    The Control API requires a credential on every route now, so this is no longer an optional read.
    It has to resolve the secret the way that Director does or every call is answered 401: the
    SHARED fleet token from the instance's config.json when the machine is attached to a Gateway,
    and the instance's own persisted token otherwise. Two past shapes of this bug: reading only the
    token file (wrong on every Gateway-attached machine), and reading the right pair of files from
    the FLAT root - which on a clean install holds neither, because every Director's storage lives
    under its own instances/<slug> home.

    The raw machine secret is what is presented, deliberately. It is full authority, which is what
    reading and writing settings needs, and the Director accepts it as the root it is.
    """
    if explicit:
        return explicit

    cfg = _config_json(home)
    if cfg.is_file():
        try:
            data = json.loads(cfg.read_text(encoding="utf-8"))
            gateway = data.get("gateway") if isinstance(data, dict) else None
            if isinstance(gateway, dict):
                token = gateway.get("token")
                if isinstance(token, str) and token.strip():
                    return token.strip()
        except (OSError, ValueError):
            pass

    tf = _token_file(home)
    if tf.is_file():
        return tf.read_text(encoding="utf-8").strip() or None
    return None


def _connect(explicit_token: str | None) -> tuple[str, str | None, Path]:
    """Discover the Director to talk to and the credential IT accepts: (endpoint, token, home)."""
    base, home = discover_director()
    return base, _resolve_token(explicit_token, home), home


def get_settings(token: str | None) -> dict:
    base, resolved, home = _connect(token)
    return _request("GET", f"{base}/settings", resolved, token_file_hint=_token_file(home))


def put_settings(patch: dict, token: str | None) -> dict:
    base, resolved, home = _connect(token)
    return _request("PUT", f"{base}/settings", resolved, body=patch, token_file_hint=_token_file(home))


def detect(kind: str, apply: bool, token: str | None) -> dict:
    """POST /settings/detect/{kind}. With apply=True the Director writes the detected value."""
    base, resolved, home = _connect(token)
    q = "?apply=true" if apply else ""
    return _request("POST", f"{base}/settings/detect/{kind}{q}", resolved, token_file_hint=_token_file(home))


def test_gateway(url: str, token: str | None) -> dict:
    """POST /settings/test/gateway - probe a gateway URL's /healthz."""
    base, resolved, home = _connect(token)
    return _request("POST", f"{base}/settings/test/gateway", resolved, body={"url": url},
                    token_file_hint=_token_file(home))


def _dig(obj: dict, dotted: str):
    cur = obj
    for part in dotted.split("."):
        if not isinstance(cur, dict) or part not in cur:
            return None
        cur = cur[part]
    return cur


def _nest(dotted: str, value) -> dict:
    parts = dotted.split(".")
    out: dict = {}
    cur = out
    for p in parts[:-1]:
        cur[p] = {}
        cur = cur[p]
    cur[parts[-1]] = value
    return out


def main() -> int:
    parser = argparse.ArgumentParser(description="Configure CC Director settings via REST.")
    parser.add_argument("--director-token", default=None, help="Bearer token (only if auth is on).")
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("show", help="Print the full config.json.")

    p_get = sub.add_parser("get", help="Print one dotted-key value.")
    p_get.add_argument("key")

    p_set = sub.add_parser("set", help="Set one dotted-key value.")
    p_set.add_argument("key")
    p_set.add_argument("value")

    p_shots = sub.add_parser("set-screenshots", help="Set the screenshots source directory.")
    p_shots.add_argument("path")

    p_gw = sub.add_parser("set-gateway", help="Set gateway connection settings.")
    p_gw.add_argument("--url", required=True, help="Gateway base URL, e.g. http://gw-host:7878")
    p_gw.add_argument("--advertised", default=None,
                      help="This Director's reachable URL (gateway calls back here).")
    p_gw.add_argument("--token", default=None, help="Gateway shared token (optional).")

    p_dg = sub.add_parser("detect-gateway", help="Scan the tailnet + loopback for a gateway.")
    p_dg.add_argument("--apply", action="store_true", help="Write the found URL to gateway.url (re-registers live).")

    p_dp = sub.add_parser("detect-public-url", help="Detect this Director's advertised public URL.")
    p_dp.add_argument("--apply", action="store_true", help="Write it to gateway.tailnetEndpoint (re-registers live).")

    p_ds = sub.add_parser("detect-screenshots", help="Detect the OS screenshots folder.")
    p_ds.add_argument("--apply", action="store_true", help="Write it to screenshots.source_directory.")

    p_tg = sub.add_parser("test-gateway", help="Probe a gateway URL's /healthz.")
    p_tg.add_argument("--url", required=True, help="Gateway base URL to test.")

    args = parser.parse_args()
    # The credential is resolved per call, AFTER discovery, from the home of the very instance the
    # discovered endpoint belongs to - an explicit --director-token still overrides it.
    token = args.director_token

    try:
        if args.command == "show":
            print(json.dumps(get_settings(token), indent=2))

        elif args.command == "get":
            value = _dig(get_settings(token), args.key)
            if value is None:
                print(f"(not set) {args.key}")
            else:
                print(value if isinstance(value, str) else json.dumps(value, indent=2))

        elif args.command == "set":
            merged = put_settings(_nest(args.key, args.value), token)
            print(f"OK set {args.key}")
            print(json.dumps(_dig(merged, args.key.split('.')[0]), indent=2))

        elif args.command == "set-screenshots":
            put_settings({"screenshots": {"source_directory": args.path}}, token)
            print(f"OK screenshots.source_directory = {args.path}")

        elif args.command == "set-gateway":
            gw = {"url": args.url}
            if args.advertised is not None:
                gw["tailnetEndpoint"] = args.advertised
            if args.token is not None:
                gw["token"] = args.token
            merged = put_settings({"gateway": gw}, token)
            print("OK gateway updated (Director re-registered live)")
            print(json.dumps(merged.get("gateway", {}), indent=2))

        elif args.command == "detect-gateway":
            r = detect("gateway", args.apply, token)
            found = r.get("found")
            if found:
                print(f"OK found gateway: {found}" + (" (applied to gateway.url)" if r.get("applied") else ""))
            else:
                print(f"(none) no gateway answered on {len(r.get('scanned', []))} address(es) scanned")

        elif args.command == "detect-public-url":
            r = detect("public-url", args.apply, token)
            url = r.get("url")
            if url:
                print(f"OK public URL: {url} ({r.get('kind')})" + (" (applied to gateway.tailnetEndpoint)" if r.get("applied") else ""))
            else:
                print("(none) no Tailscale identity or reachable address found")

        elif args.command == "detect-screenshots":
            r = detect("screenshots", args.apply, token)
            d = r.get("directory")
            if d:
                print(f"OK screenshots folder: {d}" + (" (applied to screenshots.source_directory)" if r.get("applied") else ""))
            else:
                print("(none) could not detect a screenshots folder")

        elif args.command == "test-gateway":
            r = test_gateway(args.url, token)
            # message already starts with "OK:" on success; only mark failures.
            print(str(r.get("message")) if r.get("ok") else "FAIL: " + str(r.get("message")))

    except RuntimeError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
