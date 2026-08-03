"""The ONE door the command line uses to reach the fleet: the Gateway.

Remove-the-network-port mission, phase 2. This module replaces `director.py` and
`director_token.py`, which called this machine's Director over a loopback TCP port and derived a
credential from the machine secret on disk. Both are gone. A cc-* command now calls the Gateway
and nothing else.

A session is launched with these two values, stamped as a PAIR or not at all (SessionManager):

  CC_GATEWAY_URL          - the Gateway this account is attached to
  CC_GATEWAY_SESSION_KEY  - THIS session's own key for it (phase 1b)

THERE IS NO SECOND PATH, AND THAT IS THE POINT OF THE MISSION. Nothing here tries the Gateway and
falls back to a local Director: a fallback is the second door this mission exists to remove, and an
agent that can reach the fleet two ways will use the wrong one. If the Gateway cannot be reached the
command FAILS, loudly, with a sentence naming the self-hosted gateway as the answer. The owner has
ruled on that cost: no Gateway means no agent tooling.

The key is bound to one session and one tenant and is limited to the fleet's agent routes
(`SessionKeyGuard`). It replaces two strictly wider credentials the command line used to hold: the
Director's machine secret, which `director_token.py` read off disk and minted a full-authority `cli`
scope from, and - on the skill, workflow, schedule and mission paths - the account-wide
`gateway.token`, which those tools read from config.json and presented to the Gateway directly.
"""

import json
import os
import urllib.error
import urllib.request
from typing import Any, Dict, List, Optional, Tuple

#: Response header the Gateway stamps when IT answered but a DIRECTOR is the one that failed
#: (TunnelCatchAllDispatch). Without it a 502 or 504 reads as "the Gateway is unreachable", which is
#: what an edge proxy in front of a dead Gateway means by the same code. Its ABSENCE is the safe
#: default: only a stamped response is treated as proof the Gateway itself answered.
FAULT_HEADER = "X-DevThrottle-Fault"
FAULT_DIRECTOR = "director"

#: The sentence every "there is no Gateway here" failure ends with. One wording, one place, because
#: this is the mission's accepted cost and the user must always be told the same remedy.
NO_GATEWAY_REMEDY = (
    "Install the self-hosted gateway and attach this machine to it - "
    "the fleet commands work through the Gateway and have no local path."
)


class GatewayError(RuntimeError):
    """A clear, user-facing failure talking to the Gateway (no stack traces for the agent)."""


def gateway_base_url() -> str:
    """The Gateway this session was told to call. Never guessed, never defaulted to loopback.

    A default would be a second door wearing the first one's clothes: a machine that happens to run
    a Gateway on loopback would work, one that does not would fail with a connection error instead
    of the sentence written for it, and neither user would learn what is actually required.
    """
    url = os.environ.get("CC_GATEWAY_URL", "").strip()
    if not url:
        raise GatewayError(
            "CC_GATEWAY_URL is not set, so there is no Gateway to call. These commands only work "
            f"inside a DevThrottle session on a machine attached to a Gateway. {NO_GATEWAY_REMEDY}"
        )
    return url.rstrip("/")


def session_key() -> str:
    """This session's own Gateway key.

    Stamped beside CC_GATEWAY_URL as a pair, so in practice a session has both or neither. The two
    are still checked separately and reported separately: a session holding one without the other is
    a bug in the stamping, and collapsing the two messages would hide which half went missing.
    """
    key = os.environ.get("CC_GATEWAY_SESSION_KEY", "").strip()
    if not key:
        raise GatewayError(
            "CC_GATEWAY_SESSION_KEY is not set, so this session has no credential for the Gateway. "
            "A DevThrottle session is given one at launch when its machine is attached to a "
            f"Gateway. {NO_GATEWAY_REMEDY}"
        )
    return key


def session_id() -> Optional[str]:
    """This session's own id, or None outside a session."""
    sid = os.environ.get("CC_SESSION_ID", "").strip()
    return sid or None


def _error_message(body: str, code: int, fault_is_director: bool = False) -> str:
    """What the user reads when a Gateway call fails.

    TWO body shapes arrive here and BOTH carry the sentence the server wrote. Handlers return
    `{"error": "..."}`; anything reaching ASP.NET's unhandled-exception page returns problem-details,
    which puts the sentence in `detail`. Reading only `error` is how a written refusal - naming the
    agent, the command and the fix - travels across HTTP intact and is thrown away by the last
    component, leaving the user a bare status code.

    problem-details `title` is deliberately NOT read as that sentence. It is a generic label ("An
    error occurred while processing your request.", "Not Found") that says LESS than the status code
    it would displace. It is worth showing beside the code, never instead of it.

    `fault_is_director` names the machine, not the Gateway, when the Gateway stamped the answer as a
    Director-side failure. Same status code, different truth - and an agent told "the Gateway is
    down" when one machine went quiet will go and debug the wrong thing.
    """
    who = "the machine that owns it (the Gateway answered)" if fault_is_director else "the Gateway"
    status = f"HTTP {code} from {who}"
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
    url = f"{gateway_base_url()}/{path.lstrip('/')}"
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Accept", "application/json")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    req.add_header("Authorization", f"Bearer {session_key()}")

    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8")
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as err:
        try:
            detail = err.read().decode("utf-8")
        except OSError:
            detail = ""
        fault = ""
        try:
            fault = (err.headers.get(FAULT_HEADER) or "") if err.headers else ""
        except AttributeError:
            fault = ""
        raise GatewayError(
            _error_message(detail, err.code, fault_is_director=(fault == FAULT_DIRECTOR))
        ) from err
    except urllib.error.URLError as err:
        raise GatewayError(
            f"Cannot reach the Gateway at {gateway_base_url()}: {err.reason}. "
            "Every fleet command goes through it and there is no local path to fall back to. "
            "Check that the Gateway is running and this machine can reach it."
        ) from err


def get_json(path: str, timeout: float = 30) -> Any:
    return _request("GET", path, None, timeout=timeout)


def post_json(path: str, body: Optional[dict] = None, timeout: float = 30) -> Any:
    return _request("POST", path, body if body is not None else {}, timeout=timeout)


def patch_json(path: str, body: dict, timeout: float = 30) -> Any:
    return _request("PATCH", path, body, timeout=timeout)


def delete(path: str, timeout: float = 30) -> Any:
    return _request("DELETE", path, None, timeout=timeout)


# --- The fleet roster, and the verdicts that come with it ---------------------------------------


def get_fleet() -> Tuple[List[Dict[str, Any]], Optional[bool], Optional[str], Optional[str]]:
    """The fleet roster and the Gateway's folded cautions about it.

    Returns (sessions, complete, reason, stale_answer_caution).

    * `complete` is None for "the Gateway did not say" - and None is NOT True. A Gateway that has
      not been updated still serves the envelope without these fields, and reading its silence as a
      guarantee would reinstate the defect this closes: a roster that dropped an unreachable
      machine's rows and still answered 200, so absent read identical to empty.
    * `stale_answer_caution` is the NEGATIVE-ANSWER caution, printed only when the caller's own
      lookup came back with nothing. A machine that is connected but has not reported recently can
      be hiding what was asked for, and that is the single case where its staleness changes the
      answer - a lookup that FOUND its target was plainly not hidden from. Printing it on a positive
      answer is what teaches people to stop reading cautions, which is why it is a separate value
      and not folded into `reason`.

    THERE IS EXACTLY ONE FETCH AND IT RETURNS EVERY FIELD. An opt-in second helper carrying the
    newer field existed once, and only one tool called it, so every other verb answered "nothing
    matched" with no idea a stale machine might be hiding the answer. Widening this one and letting
    a value go unread at a site that does not need it is not the drift; a second helper is.

    Every verdict here is FOLDED ON THE GATEWAY and printed verbatim. Deciding what "offline" means
    for completeness is a ruling, and rulings do not live in a client.
    """
    body = get_json("sessions?envelope=true") or []
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
    """The sentence to add when the roster may not be trustworthy end to end. Empty when it is.

    The fallback wordings are reached only when the Gateway supplied no sentence of its own.
    """
    if complete is True:
        return ""
    if complete is False:
        return reason or "Part of the fleet could not be reached, so some of these sessions may be out of date."
    return "The Gateway cannot confirm the roster is complete, so a session may be missing from it."


def no_match_message(target: str) -> str:
    """The shared 'no session matches' line, so the tools cannot drift on the wording."""
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

    An exactly-three-digit token (the session number) is matched against session numbers first; if
    one or more active sessions hold that number, those are returned. Numbers are unique among
    active sessions, so this normally yields exactly one match. When no active session holds the
    number, resolution falls back to id / name matching, so a three-digit token that happens to be
    an id prefix still resolves.

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
