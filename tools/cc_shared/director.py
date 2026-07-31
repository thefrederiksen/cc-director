"""Shared helpers for tools that talk to their OWN Director's Control API (issue #705).

A cc-director session is launched with two environment values these tools rely on:

  CC_DIRECTOR_API  - the base URL of this session's own Director Control API (loopback)
  CC_SESSION_ID    - this session's GUID

For the helpers in THIS module, cc-devthrottle only ever calls its own Director (loopback). The
Director relays to the Gateway on its behalf, so these helpers need neither the Gateway URL nor the
fleet token - the fleet token stays on the Director.

Note: the separate schedule path (schedule_ops.py / the `schedule-*` commands) is deliberately
different - it resolves the configured gateway.url and gateway.token and calls the Gateway's
cron surface directly. That path therefore does need the Gateway URL and token; the
"never needs the Gateway URL or the fleet token" rule above applies only to the Director-relay
helpers defined here, not to schedule_ops.
"""

import json
import os
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple


class DirectorError(RuntimeError):
    """A clear, user-facing failure talking to the Director (no stack traces for the agent)."""


def director_base_url() -> str:
    url = os.environ.get("CC_DIRECTOR_API", "").strip()
    if not url:
        raise DirectorError(
            "CC_DIRECTOR_API is not set. These tools only work inside a cc-director session."
        )
    return url.rstrip("/")


def session_id() -> Optional[str]:
    sid = os.environ.get("CC_SESSION_ID", "").strip()
    return sid or None


def _token() -> Optional[str]:
    # The loopback Control API needs NO token in the default configuration. In LAN mode the
    # Director's own standalone token (gateway-token.txt) is accepted; send it best-effort if
    # present. The fleet token is deliberately never read here - it stays on the Director.
    local = os.environ.get("LOCALAPPDATA", "")
    if not local:
        return None
    token_file = Path(local) / "cc-director" / "config" / "director" / "gateway-token.txt"
    try:
        if token_file.is_file():
            value = token_file.read_text(encoding="utf-8").strip()
            return value or None
    except OSError:
        return None
    return None


def _error_message(body: str, code: int) -> str:
    """What the user reads when a Director call fails (issue #1062).

    TWO body shapes arrive here and BOTH carry the sentence the Director wrote. Its own handlers
    return `{"error": "..."}`. Anything that reaches ASP.NET's unhandled-exception page instead
    returns problem-details, which puts the sentence in `detail` - and the old extractor read only
    `error`, so every problem-details failure arrived as a bare status code. That is how the
    refusal sentence added in #1050 - naming the agent, the command tried, and the two ways to fix
    it - was written, travelled across HTTP intact, and was thrown away by the last component: the
    user saw `HTTP 500 from the Director`, indistinguishable from the bug #1050 had just closed.
    This is a SHARED helper, so the loss was never spawn-only; it applied to every command routed
    through `_request` that hit an endpoint answering in problem-details.

    problem-details `title` is deliberately NOT read as that sentence. It is a generic label - "An
    error occurred while processing your request.", "Not Found" - that says LESS than the status
    code it would have displaced, so promoting it would trade one uninformative line for a worse
    one. It is worth showing beside the code, never instead of it.
    """
    status = f"HTTP {code} from the Director"
    try:
        obj = json.loads(body)
    except (ValueError, TypeError):
        return status
    if not isinstance(obj, dict):
        return status
    for key in ("error", "Error", "detail", "Detail"):
        value = obj.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    for key in ("title", "Title"):
        value = obj.get(key)
        if isinstance(value, str) and value.strip():
            return f"{status}: {value.strip()}"
    return status


def _request(method: str, path: str, body: Optional[dict] = None, timeout: float = 30) -> Any:
    url = f"{director_base_url()}/{path.lstrip('/')}"
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Accept", "application/json")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    token = _token()
    if token:
        req.add_header("Authorization", f"Bearer {token}")

    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8")
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as err:
        detail = ""
        try:
            detail = err.read().decode("utf-8")
        except OSError:
            detail = ""
        raise DirectorError(_error_message(detail, err.code)) from err
    except urllib.error.URLError as err:
        raise DirectorError(
            f"Cannot reach the Director at {director_base_url()}: {err.reason}"
        ) from err


def get_json(path: str) -> Any:
    return _request("GET", path)


def post_json(path: str, body: dict, timeout: float = 30) -> Any:
    return _request("POST", path, body, timeout=timeout)


def patch_json(path: str, body: dict, timeout: float = 30) -> Any:
    return _request("PATCH", path, body, timeout=timeout)


def delete(path: str, timeout: float = 30) -> Any:
    return _request("DELETE", path, None, timeout=timeout)


def get_fleet() -> Tuple[List[Dict[str, Any]], Optional[bool], Optional[str]]:
    """The fleet roster, plus whether the Director could vouch that it is COMPLETE (issue #1051).

    The Gateway used to drop an unreachable Director's sessions and still answer 200, so a short roster
    was indistinguishable from a whole one. Since epic #1159 step A it SERVES those rows instead, dimmed
    and dated, so the doubt has moved rather than gone: the list is no longer short, but an unreachable
    Director's rows are the last thing it said rather than a confirmed present state. Asking for the
    envelope returns the Director's folded verdict alongside the rows; the verdict is computed there,
    never here, because deciding what a reachability state MEANS is ruling and a client only renders.

    Returns (sessions, complete, reason). `complete` is None for "the Director did not say" - and
    None is NOT True: a Director that has not restarted since this was added still serves the bare
    array, and reading its silence as a guarantee would reinstate the very defect this closes.

    Lives here, not in one tool, because three tools resolve a target against this roster and each
    used to print "No session matches" with no idea the list might be short. Three copies of that
    judgement would drift; one cannot.
    """
    body = get_json("fleet/sessions?envelope=true") or []
    if isinstance(body, list):
        return body, None, None
    sessions = body.get("sessions") or []
    complete = body.get("rosterComplete")
    reason = body.get("rosterIncompleteReason")
    return sessions, (complete if isinstance(complete, bool) else None), reason


def get_fleet_with_stale_caution():
    """The roster, its completeness verdict, and the NEGATIVE-ANSWER caution (all folded by the Director).

    The third value is printed ONLY when the caller's own lookup came back empty. A machine that is
    connected but has not reported recently can be hiding what was asked for, and that is the single case
    where its staleness changes the answer - a lookup that FOUND what it wanted was plainly not hidden
    from. Printing it unconditionally is what teaches people to stop reading cautions, which is why it is
    a separate value and not folded into the completeness reason.
    """
    body = get_json("fleet/sessions?envelope=true") or []
    if isinstance(body, list):
        return body, None, None, None
    sessions = body.get("sessions") or []
    complete = body.get("rosterComplete")
    reason = body.get("rosterIncompleteReason")
    stale = body.get("rosterStaleAnswerCaution")
    return (
        sessions,
        (complete if isinstance(complete, bool) else None),
        reason,
        stale if isinstance(stale, str) and stale.strip() else None,
    )


def roster_caveat(complete: Optional[bool], reason: Optional[str]) -> str:
    """The sentence to add when the roster may not be trustworthy end to end. Empty when it is (#1051).

    The fallback wordings below are only reached when the Gateway supplied no sentence of its own, and
    they must not out-live the Gateway's behaviour: since #1159 step A an unreachable machine's sessions
    are LISTED rather than dropped, so the caution is about how much the rows can be relied on, not
    about rows being absent.
    """
    if complete is True:
        return ""
    if complete is False:
        return reason or "Part of the fleet could not be reached, so some of these sessions may be out of date."
    return "This Director cannot confirm the roster is complete, so a session may be missing from it."


def no_match_message(target: str) -> str:
    """The shared 'no session matches' line, so the three tools cannot drift on the wording."""
    return (f"[red]No session matches '{target}'.[/red] "
            "Run cc-devthrottle session list to see the fleet.")


def field(dto: Dict[str, Any], *keys: str, default: str = "") -> str:
    """Read the first present key from a session DTO, tolerating camelCase or PascalCase.

    Returns a STRING always - so never use it to read a boolean: `str(False)` is `"False"`, which is
    truthy, and every row would test true. Read booleans straight off the dict instead.
    """
    for key in keys:
        if key in dto and dto[key] is not None:
            return str(dto[key])
    return default


def short_id(session_guid: str) -> str:
    return session_guid[:8] if len(session_guid) > 8 else session_guid


def resolve_target(sessions: List[Dict[str, Any]], query: str) -> List[Dict[str, Any]]:
    """Resolve a user-typed target to matching sessions.

    Issue #821: an exactly-three-digit token (the session number, 100-999 from issue #820) is
    matched against session numbers first; if one or more active sessions hold that number, those
    are returned. Numbers are unique among active sessions (#820), so this normally yields exactly
    one match. When no active session holds the number, resolution falls back to id / name matching
    as before, so a three-digit token that happens to be an id prefix still resolves.

    Otherwise a full id match wins outright; failing that, match by id prefix OR by exact
    (case-insensitive) name. Returns the de-duplicated list of matches so the caller can detect
    ambiguity (more than one) or no match (empty).
    """
    q = query.strip().lower()
    if not q:
        return []

    if q.isdigit() and 100 <= int(q) <= 999:
        wanted = str(int(q))
        by_number = [s for s in sessions if field(s, "number", "Number") == wanted]
        if by_number:
            return by_number

    for s in sessions:
        if field(s, "sessionId", "SessionId").lower() == q:
            return [s]

    matches: Dict[str, Dict[str, Any]] = {}
    for s in sessions:
        sid = field(s, "sessionId", "SessionId")
        name = field(s, "name", "Name")
        if sid.lower().startswith(q) or name.lower() == q:
            matches[sid] = s
    return list(matches.values())
