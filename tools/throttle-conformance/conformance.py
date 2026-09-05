"""The Your Throttle conformance check (mission "Clean up Your Throttle", phase three).

Two consumers read one substrate - the Gateway's submission ledger (activity_events, turn-submitted) - and
this check FAILS when they diverge. It computes the shared figure twice, over the same account and the
same week, and compares every number:

  1. THE LIBRARY: the Gateway's own ThrottleDefinition and ThrottleLedgerReader, the code behind
     GET /stats/data, run by tools/throttle-conformance against the hosted Gateway database (read-only).
  2. THE MENTOR REPORT'S SIDE: the mentor harness's own reader of the same ledger (tools/mentor/origin.py
     Ledger over tools/mentor/metrics.py load_events), fed from the harness's own extract of the table,
     with ruling R17's predicate applied - InputOrigin present, grouped by modality and surface.

A third, deliberately plain reading straight off the extract's JSON lines (no library, no harness) covers
what the mentor's reader does not carry - the per-agent split and the per-repository join through
session_history - so those are checked too.

The predicate is stated once, in the Gateway (ThrottleDefinition.Predicate), and the check ALSO asserts
the library reports that exact sentence. Nothing here paraphrases it.

Usage:
  python tools/throttle-conformance/conformance.py --account soren --week 2026-W35
  python tools/throttle-conformance/conformance.py --account mario --week 2026-W34 --report out.md

Options:
  --account   a label from the mentor config's accounts (soren, mario)
  --week      an ISO week, in the account's time zone, Monday to Monday (the mentor's own week bounds)
  --mentor-dir      the mentor harness directory holding config.json, metrics.py and origin.py
                    (default: D:/ReposFred/devthrottle_internal/tools/mentor)
  --connection-file a file holding the Gateway database connection string (default: the
                    DEVTHROTTLE_GATEWAY_DB_CONNECTION key from the credentials file named in the mentor config)
  --library-json    skip running the library and read its figure from this file (for re-checks)
  --report          write a markdown report here as well as printing the verdict
  --break-predicate DELIBERATELY misapply the predicate on the mentor side (drop null-send-source rows), to
                    prove the check goes red. Never green with this flag.

Exit 0 when every number agrees. Exit 1 on any difference. Exit 2 on a usage or setup error.
"""
import argparse
import collections
import datetime as dt
import json
import os
import subprocess
import sys
from pathlib import Path
from zoneinfo import ZoneInfo

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
DEFAULT_MENTOR = Path("D:/ReposFred/devthrottle_internal/tools/mentor")


def fail(msg, code=2):
    print("ERROR: " + msg, file=sys.stderr)
    sys.exit(code)


def load_mentor(mentor_dir):
    sys.path.insert(0, str(mentor_dir))
    cwd = os.getcwd()
    os.chdir(mentor_dir)
    try:
        import metrics  # noqa: E402
        import origin   # noqa: E402
    finally:
        os.chdir(cwd)
    return metrics, origin


def read_connection(args, cfg):
    if args.connection_file:
        return Path(args.connection_file).read_text(encoding="utf-8").strip()
    env_path = Path(os.path.expandvars(cfg["credentials_env"]))
    key = cfg["db_connection_key"]
    if not env_path.exists():
        fail("credentials file not found: " + str(env_path))
    for line in env_path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line.startswith(key + "="):
            return line[len(key) + 1:].strip().strip('"')
    fail("key " + key + " not found in " + str(env_path))


def run_library(tenant, start, end, connection, out_path):
    """Run the Gateway's own definition through tools/throttle-conformance. Returns the figure dict."""
    dll = HERE / "bin" / "Debug" / "net10.0" / "throttle-conformance.dll"
    if not dll.exists():
        build = subprocess.run(["dotnet", "build", str(HERE / "ThrottleConformance.csproj"), "-nologo", "-v", "q"],
                               capture_output=True, text=True)
        if build.returncode != 0:
            fail("building the library tool failed:\n" + build.stdout + build.stderr)
    cmd = ["dotnet", str(dll), "--tenant", tenant, "--from", start.isoformat(), "--to", end.isoformat(),
           "--connection", connection, "--out", str(out_path)]
    run = subprocess.run(cmd, capture_output=True, text=True)
    # stderr carries the one summary line (and never the connection string); surface it.
    for line in run.stderr.splitlines():
        print("  [library] " + line)
    if run.returncode != 0:
        fail("the library tool exited " + str(run.returncode), code=1)
    return json.loads(Path(out_path).read_text(encoding="utf-8"))


def mentor_side(metrics, origin, account, tz, extract, start, end, break_predicate):
    """The mentor harness's reading of the same ledger, with the R17 predicate applied."""
    world = metrics.World(account["label"], tz)
    metrics.load_events(world, extract)
    ledger = origin.Ledger(world.events)
    rows = [r for rows in ledger.by_session.values() for r in rows if start <= r["ts"] < end]
    buckets = collections.Counter()
    excluded = collections.Counter()
    for r in rows:
        if break_predicate and r["source"] is None:
            # THE KNOWN-BAD INPUT: pretend the terminal-typed turns (null send source) are not turns. This is
            # exactly defect one, and the check must go red on it.
            excluded["noInputOrigin"] += 1
            excluded["unresolved"] += 1
            continue
        if r["origin"] is None:
            excluded["noInputOrigin"] += 1
            if r["source"] == "Agent":
                excluded["agentDriven"] += 1
            elif r["source"] == "Framework":
                excluded["framework"] += 1
            else:
                excluded["unresolved"] += 1
            continue
        modality, surface = r["origin"]
        buckets[(modality, surface)] += 1
    # The mentor's Ledger keys rows by session already; count the sessions that had a counted turn.
    counted_sessions = {
        s for s, srows in ledger.by_session.items()
        if any(start <= r["ts"] < end and r["origin"] is not None and not (break_predicate and r["source"] is None)
               for r in srows)
    }
    return {
        "turns": sum(buckets.values()),
        "voiceTurns": sum(v for (m, _), v in buckets.items() if m == "voice"),
        "typedTurns": sum(v for (m, _), v in buckets.items() if m == "typed"),
        "sessions": len(counted_sessions),
        "buckets": {m + "/" + s: v for (m, s), v in sorted(buckets.items())},
        "excluded": {k: excluded.get(k, 0) for k in ("noInputOrigin", "agentDriven", "framework", "unresolved")},
        "agentDrivenTurns": excluded.get("agentDriven", 0),
        "rowsInWindow": len(rows),
    }


def raw_side(extract_events, extract_sessions, start, end):
    """A plain reading of the extract for what the mentor's Ledger does not carry: agent kind and repository."""
    def parse(s):
        return dt.datetime.fromisoformat(s.replace("Z", "+00:00"))
    history = {}
    with open(extract_sessions, encoding="utf-8") as f:
        for line in f:
            row = json.loads(line)
            history[row["SessionId"]] = (row.get("RepoName") or None, row.get("RepoPath") or None)
    agents = collections.defaultdict(lambda: {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": set(), "agentDrivenTurns": 0})
    repos = collections.defaultdict(lambda: {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": set()})
    unattributed = 0
    hours = collections.Counter()
    with open(extract_events, encoding="utf-8") as f:
        for line in f:
            row = json.loads(line)
            if row.get("EventType") != "turn-submitted":
                continue
            ts = parse(row["OccurredUtc"])
            if not (start <= ts < end):
                continue
            agent = row.get("AgentKind") or ""
            origin_token = row.get("InputOrigin")
            if not origin_token:
                if row.get("SendSource") == "Agent":
                    agents[agent]["agentDrivenTurns"] += 1
                continue
            modality = origin_token.split("/", 1)[0]
            a = agents[agent]
            a["turns"] += 1
            a["voiceTurns" if modality == "voice" else "typedTurns"] += 1
            a["sessions"].add(row["SessionId"])
            hours[ts.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H")] += 1
            name, path = history.get(row["SessionId"], (None, None))
            if name:
                key = name
            elif path:
                key = path.rstrip("/\\").replace("\\", "/").split("/")[-1]
            else:
                unattributed += 1
                continue
            r = repos[key]
            r["turns"] += 1
            r["voiceTurns" if modality == "voice" else "typedTurns"] += 1
            r["sessions"].add(row["SessionId"])
    return {
        "agents": {k: {"turns": v["turns"], "voiceTurns": v["voiceTurns"], "typedTurns": v["typedTurns"],
                       "sessions": len(v["sessions"]), "agentDrivenTurns": v["agentDrivenTurns"]}
                   for k, v in agents.items()},
        "repos": {k: {"turns": v["turns"], "voiceTurns": v["voiceTurns"], "typedTurns": v["typedTurns"],
                      "sessions": len(v["sessions"])} for k, v in repos.items()},
        "reposUnattributedTurns": unattributed,
        "hourlyTurns": dict(hours),
    }


def compare(library, mentor, raw, predicate_expected):
    """Every difference, as a list of one-line strings. Empty means the consumers agree."""
    diffs = []

    def eq(name, a, b):
        if a != b:
            diffs.append("%s: library=%s mentor-side=%s" % (name, a, b))

    if library.get("definition") != predicate_expected:
        diffs.append("definition: the library does not report the R17 predicate verbatim: %r" % library.get("definition"))
    eq("turns", library["turns"], mentor["turns"])
    eq("voiceTurns", library["voiceTurns"], mentor["voiceTurns"])
    eq("typedTurns", library["typedTurns"], mentor["typedTurns"])
    eq("sessions", library["sessions"], mentor["sessions"])
    lib_buckets = {b["modality"] + "/" + b["surface"]: b["turns"] for b in library["buckets"]}
    for key in sorted(set(lib_buckets) | set(mentor["buckets"])):
        eq("bucket " + key, lib_buckets.get(key, 0), mentor["buckets"].get(key, 0))
    for key in ("noInputOrigin", "agentDriven", "framework", "unresolved"):
        eq("excluded." + key, library["excluded"][key], mentor["excluded"][key])
    eq("agentDrivenTurns", library["agentDrivenTurns"], mentor["agentDrivenTurns"])

    lib_agents = {a["agent"]: a for a in library["agents"]}
    for key in sorted(set(lib_agents) | set(raw["agents"])):
        la = lib_agents.get(key, {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": 0, "agentDrivenTurns": 0})
        ra = raw["agents"].get(key, {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": 0, "agentDrivenTurns": 0})
        for field in ("turns", "voiceTurns", "typedTurns", "sessions", "agentDrivenTurns"):
            if la[field] != ra[field]:
                diffs.append("agent %s.%s: library=%s raw-extract=%s" % (key or "(unknown)", field, la[field], ra[field]))
    lib_repos = {r["repo"]: r for r in library["repos"]}
    for key in sorted(set(lib_repos) | set(raw["repos"])):
        lr = lib_repos.get(key, {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": 0})
        rr = raw["repos"].get(key, {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": 0})
        for field in ("turns", "voiceTurns", "typedTurns", "sessions"):
            if lr[field] != rr[field]:
                diffs.append("repo %s.%s: library=%s raw-extract=%s" % (key, field, lr[field], rr[field]))
    if library["reposUnattributedTurns"] != raw["reposUnattributedTurns"]:
        diffs.append("reposUnattributedTurns: library=%s raw-extract=%s" % (library["reposUnattributedTurns"], raw["reposUnattributedTurns"]))
    lib_hours = {h["hour"]: h["turns"] for h in library["hourlyTurns"]}
    for key in sorted(set(lib_hours) | set(raw["hourlyTurns"])):
        if lib_hours.get(key, 0) != raw["hourlyTurns"].get(key, 0):
            diffs.append("hour %s: library=%s raw-extract=%s" % (key, lib_hours.get(key, 0), raw["hourlyTurns"].get(key, 0)))
    return diffs


def share(voice, total):
    return "%.2f%%" % (100.0 * voice / total) if total else "n/a"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--account", required=True)
    ap.add_argument("--week", required=True, help="ISO week, e.g. 2026-W35")
    ap.add_argument("--mentor-dir", default=str(DEFAULT_MENTOR))
    ap.add_argument("--connection-file")
    ap.add_argument("--library-json")
    ap.add_argument("--report")
    ap.add_argument("--break-predicate", action="store_true")
    args = ap.parse_args()

    mentor_dir = Path(args.mentor_dir)
    if not (mentor_dir / "config.json").exists():
        fail("no config.json in " + str(mentor_dir))
    cfg = json.loads((mentor_dir / "config.json").read_text(encoding="utf-8"))
    account = next((a for a in cfg["accounts"] if a["label"] == args.account), None)
    if account is None:
        fail("account '%s' is not in the mentor config (%s)" % (args.account, ", ".join(a["label"] for a in cfg["accounts"])))
    tz = account["time_zone"]
    tenant = account["tenant_id"]
    data_root = Path(cfg["data_root"]) / "accounts" / account["label"] / "raw" / "db"
    extract_events = data_root / "activity_events.jsonl"
    extract_sessions = data_root / "session_history.jsonl"
    for p in (extract_events, extract_sessions):
        if not p.exists():
            fail("mentor extract missing: " + str(p))

    metrics, origin = load_mentor(mentor_dir)
    start, end = metrics.week_bounds(args.week, tz)
    start_utc = start.astimezone(dt.timezone.utc)
    end_utc = end.astimezone(dt.timezone.utc)
    print("account %s (tenant %s, %s)  week %s = %s .. %s" % (account["label"], tenant, tz, args.week,
                                                             start_utc.isoformat(), end_utc.isoformat()))

    if args.library_json:
        library = json.loads(Path(args.library_json).read_text(encoding="utf-8"))
    else:
        connection = read_connection(args, cfg)
        out_path = Path(os.environ.get("TEMP", ".")) / ("throttle-library-%s-%s.json" % (account["label"], args.week))
        library = run_library(tenant, start_utc, end_utc, connection, out_path)

    mentor = mentor_side(metrics, origin, account, tz, extract_events, start, end, args.break_predicate)
    raw = raw_side(extract_events, extract_sessions, start, end)

    predicate = ("The shared figure is computed over activity_events rows where EventType is turn-submitted "
                 "and InputOrigin is present, grouped by the origin's modality and surface.")
    diffs = compare(library, mentor, raw, predicate)

    lines = []
    lines.append("# Your Throttle conformance - %s, %s" % (account["label"], args.week))
    lines.append("")
    lines.append("Window: %s to %s (%s, Monday to Monday)." % (start_utc.isoformat(), end_utc.isoformat(), tz))
    lines.append("")
    lines.append("| figure | library (Gateway code over the hosted database) | mentor side (harness reader over its extract) |")
    lines.append("|---|---:|---:|")
    for name in ("turns", "voiceTurns", "typedTurns", "sessions"):
        lines.append("| %s | %s | %s |" % (name, library[name], mentor[name]))
    lines.append("| spoken share | %s | %s |" % (share(library["voiceTurns"], library["turns"]), share(mentor["voiceTurns"], mentor["turns"])))
    lib_buckets = {b["modality"] + "/" + b["surface"]: b["turns"] for b in library["buckets"]}
    for key in sorted(set(lib_buckets) | set(mentor["buckets"])):
        lines.append("| bucket %s | %s | %s |" % (key, lib_buckets.get(key, 0), mentor["buckets"].get(key, 0)))
    for key in ("noInputOrigin", "agentDriven", "framework", "unresolved"):
        lines.append("| excluded.%s | %s | %s |" % (key, library["excluded"][key], mentor["excluded"][key]))
    lines.append("")
    lines.append("Per-agent and per-repository splits and the hourly series were compared against a plain reading of the extract; "
                 "%d agents, %d repositories, %d hours." % (len(raw["agents"]), len(raw["repos"]), len(raw["hourlyTurns"])))
    lines.append("")
    if diffs:
        lines.append("## FAIL - the consumers diverge")
        lines.append("")
        for d in diffs:
            lines.append("- " + d)
    else:
        lines.append("## PASS - every number agrees")
    if args.break_predicate:
        lines.append("")
        lines.append("(run with --break-predicate: the mentor side deliberately dropped null-send-source rows)")
    text = "\n".join(lines)
    print(text)
    if args.report:
        Path(args.report).write_text(text + "\n", encoding="utf-8")
    sys.exit(1 if diffs else 0)


if __name__ == "__main__":
    main()
