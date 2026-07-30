"""
Machine memory map.

Answers three questions without guessing:
  1. Where has the physical memory actually gone?
  2. How much of it is the Director, and how much is the sessions it runs?
  3. What is reclaimable right now, and what would reclaiming it cost?

Standalone: reads only, changes nothing, touches no Director code.

Usage:
    python memory_map.py                 console report
    python memory_map.py --html out.html console report + HTML map
"""

import argparse
import ctypes
import ctypes.wintypes as wt
import html
import os
import sys
import time
from collections import defaultdict

try:
    import psutil
except ImportError:
    sys.exit("psutil is required: python -m pip install psutil")

GB = 1024.0 ** 3
MB = 1024.0 ** 2

# How long to watch a process before calling it idle. A process that burns no
# CPU across this window is not doing work right now; combined with a dead
# parent that makes a build node a leftover rather than a participant.
IDLE_SAMPLE_SECONDS = 3.0
IDLE_CPU_THRESHOLD = 0.05


# --------------------------------------------------------------------------
# System-wide totals. Process working sets never add up to "memory in use" -
# the kernel pools and the driver-locked pages are not owned by any process,
# so a report built only from processes always under-counts and looks wrong.
# --------------------------------------------------------------------------

class PERFORMANCE_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("cb", wt.DWORD),
        ("CommitTotal", ctypes.c_size_t),
        ("CommitLimit", ctypes.c_size_t),
        ("CommitPeak", ctypes.c_size_t),
        ("PhysicalTotal", ctypes.c_size_t),
        ("PhysicalAvailable", ctypes.c_size_t),
        ("SystemCache", ctypes.c_size_t),
        ("KernelTotal", ctypes.c_size_t),
        ("KernelPaged", ctypes.c_size_t),
        ("KernelNonpaged", ctypes.c_size_t),
        ("PageSize", ctypes.c_size_t),
        ("HandleCount", wt.DWORD),
        ("ProcessCount", wt.DWORD),
        ("ThreadCount", wt.DWORD),
    ]


def system_totals():
    pi = PERFORMANCE_INFORMATION()
    pi.cb = ctypes.sizeof(pi)
    if not ctypes.windll.psapi.GetPerformanceInfo(ctypes.byref(pi), pi.cb):
        raise ctypes.WinError()
    page = pi.PageSize
    vm = psutil.virtual_memory()
    return {
        "physical_total": pi.PhysicalTotal * page,
        "physical_available": pi.PhysicalAvailable * page,
        "physical_used": (pi.PhysicalTotal - pi.PhysicalAvailable) * page,
        "commit_total": pi.CommitTotal * page,
        "commit_limit": pi.CommitLimit * page,
        "commit_peak": pi.CommitPeak * page,
        "kernel_paged": pi.KernelPaged * page,
        "kernel_nonpaged": pi.KernelNonpaged * page,
        "system_cache": pi.SystemCache * page,
        "process_count": pi.ProcessCount,
        "thread_count": pi.ThreadCount,
        "handle_count": pi.HandleCount,
        "vm_percent": vm.percent,
    }


# --------------------------------------------------------------------------
# Process snapshot
# --------------------------------------------------------------------------

FIELDS = ["pid", "ppid", "name", "create_time", "memory_info", "cmdline", "exe"]


def snapshot():
    procs = {}
    for p in psutil.process_iter(FIELDS, ad_value=None):
        try:
            info = p.info
            mi = info.get("memory_info")
            if mi is None:
                continue
            procs[info["pid"]] = {
                "pid": info["pid"],
                "ppid": info.get("ppid") or 0,
                "name": info.get("name") or "?",
                "exe": info.get("exe") or "",
                "cmdline": " ".join(info.get("cmdline") or []),
                "create_time": info.get("create_time") or 0.0,
                "rss": getattr(mi, "rss", 0),
                "private": getattr(mi, "private", 0),
                "proc": p,
            }
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    return procs


def measure_idle(procs, names):
    """Mark processes of the given names idle or busy over a sampling window."""
    targets = [d for d in procs.values() if d["name"].lower() in names]
    first = {}
    for d in targets:
        try:
            t = d["proc"].cpu_times()
            first[d["pid"]] = t.user + t.system
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
    time.sleep(IDLE_SAMPLE_SECONDS)
    for d in targets:
        if d["pid"] not in first:
            continue
        try:
            t = d["proc"].cpu_times()
            delta = (t.user + t.system) - first[d["pid"]]
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
        d["cpu_delta"] = delta
        d["idle"] = delta < IDLE_CPU_THRESHOLD


# --------------------------------------------------------------------------
# Trees
# --------------------------------------------------------------------------

def children_map(procs):
    kids = defaultdict(list)
    for d in procs.values():
        kids[d["ppid"]].append(d["pid"])
    return kids


def descendants(pid, kids):
    out, stack = [], [pid]
    while stack:
        for c in kids.get(stack.pop(), ()):
            out.append(c)
            stack.append(c)
    return out


# --------------------------------------------------------------------------
# Classification. One process belongs to exactly one bucket, so the buckets
# sum to the process total instead of double-counting shared trees.
# --------------------------------------------------------------------------

BUILD_NAMES = {"msbuild.exe", "vbcscompiler.exe", "testhost.exe", "vstest.console.exe"}
BROWSER_NAMES = {"chrome.exe", "msedge.exe", "firefox.exe", "brave.exe"}
SHELL_NAMES = {"conhost.exe", "powershell.exe", "pwsh.exe", "cmd.exe", "bash.exe",
               "git.exe", "openconsole.exe", "windowsterminal.exe"}
# Windows itself. Split out so it is obvious how much is the operating system
# and therefore not ours to reclaim.
WINDOWS_NAMES = {
    "svchost.exe", "memory compression", "memcompression", "explorer.exe", "dwm.exe",
    "runtimebroker.exe", "dllhost.exe", "wmiprvse.exe", "searchhost.exe", "secure system",
    "registry", "system", "csrss.exe", "lsass.exe", "services.exe", "wininit.exe",
    "winlogon.exe", "smss.exe", "fontdrvhost.exe", "taskhostw.exe", "sihost.exe",
    "ctfmon.exe", "textinputhost.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe",
    "applicationframehost.exe", "systemsettings.exe", "audiodg.exe", "spoolsv.exe",
    "msmpeng.exe", "nissrv.exe", "securityhealthservice.exe", "mpdefendercoreservice.exe",
    "taskmgr.exe", "smartscreen.exe", "backgroundtaskhost.exe", "widgets.exe",
}
DB_NAMES = {"sqlservr.exe", "postgres.exe", "mysqld.exe", "redis-server.exe",
            "sqlwriter.exe", "sqlceip.exe", "reportingservicesservice.exe"}
VM_NAMES = {"vmmem", "vmmemwsl", "vmwp.exe", "vmcompute.exe", "docker desktop.exe",
            "com.docker.backend.exe", "com.docker.build.exe", "wslservice.exe"}


def classify(d):
    n = d["name"].lower()
    cmd = d["cmdline"].lower()
    if n.startswith("cc-director") or n in ("devthrottle-gateway.exe", "cc-launcher.exe"):
        return "director"
    if n == "claude.exe":
        return "agent"
    if n in BUILD_NAMES:
        return "build"
    if n == "dotnet.exe":
        if "msbuild.dll" in cmd or "vstest" in cmd or " test " in cmd or cmd.endswith(" test"):
            return "build"
        return "dotnet-app"
    if n == "node.exe":
        return "mcp-node" if "mcp" in cmd else "node"
    if n in ("python.exe", "py.exe"):
        return "python"
    if n == "msedgewebview2.exe":
        return "webview"
    if n in BROWSER_NAMES:
        return "browser"
    if n in SHELL_NAMES:
        return "shell"
    if n in WINDOWS_NAMES:
        return "windows"
    if n in DB_NAMES:
        return "database"
    if n in VM_NAMES:
        return "vm-wsl-docker"
    return "desktop-apps"


# --------------------------------------------------------------------------
# Reclaim analysis
# --------------------------------------------------------------------------

def find_reclaimable(procs, kids):
    """Return a list of reclaim candidates, each with an explicit reason.

    Only two things qualify. Both are caches that exist to make the NEXT build
    faster and hold no state that matters:
      - an MSBuild worker node whose driving build process is already gone
      - a compiler server sitting idle
    Anything still burning CPU, and anything with a live parent, is left alone:
    a build that looks idle for three seconds may simply be waiting on a peer.
    """
    live = set(procs)
    out = []
    for d in procs.values():
        n = d["name"].lower()
        cmd = d["cmdline"].lower()
        idle = d.get("idle", False)
        age_min = (time.time() - d["create_time"]) / 60.0

        if n == "dotnet.exe" and "msbuild.dll" in cmd and "/nodemode:" in cmd:
            parent_dead = d["ppid"] not in live
            if parent_dead and idle:
                out.append((d, "orphaned MSBuild worker node - driving build has exited"))
            elif idle and age_min > 15:
                out.append((d, "idle MSBuild worker node - no CPU, older than 15 min"))
        elif n == "vbcscompiler.exe" and idle:
            out.append((d, "idle Roslyn compiler server - restarts on next build"))
    return out


# --------------------------------------------------------------------------
# Report
# --------------------------------------------------------------------------

def fmt(b):
    return "%.2f GB" % (b / GB) if b >= GB else "%.0f MB" % (b / MB)


def bar(value, total, width=34):
    if total <= 0:
        return " " * width
    filled = int(round(width * value / total))
    return "#" * min(filled, width) + "." * max(0, width - filled)


def build_report(html_path=None):
    sys_t = system_totals()
    procs = snapshot()
    measure_idle(procs, {"dotnet.exe", "vbcscompiler.exe", "msbuild.exe", "testhost.exe"})
    kids = children_map(procs)

    proc_rss = sum(d["rss"] for d in procs.values())
    proc_priv = sum(d["private"] for d in procs.values())
    kernel = sys_t["kernel_paged"] + sys_t["kernel_nonpaged"]
    unattributed = sys_t["physical_used"] - proc_rss - kernel

    print("=" * 78)
    print("MEMORY MAP  -  %s  -  %s" % (os.environ.get("COMPUTERNAME", "?"),
                                        time.strftime("%Y-%m-%d %H:%M:%S")))
    print("=" * 78)
    print()
    print("PHYSICAL")
    print("  Installed            %s" % fmt(sys_t["physical_total"]))
    print("  In use               %s   (%.0f%%)" % (fmt(sys_t["physical_used"]), sys_t["vm_percent"]))
    print("  Available            %s" % fmt(sys_t["physical_available"]))
    print()
    print("COMMIT (physical + page file - the real ceiling)")
    print("  Committed            %s" % fmt(sys_t["commit_total"]))
    print("  Commit limit         %s" % fmt(sys_t["commit_limit"]))
    print("  Headroom             %s" % fmt(sys_t["commit_limit"] - sys_t["commit_total"]))
    print("  Peak this boot       %s" % fmt(sys_t["commit_peak"]))
    print()
    print("WHERE THE PHYSICAL MEMORY IS")
    rows = [
        ("Process working sets", proc_rss),
        ("Kernel paged pool", sys_t["kernel_paged"]),
        ("Kernel nonpaged pool", sys_t["kernel_nonpaged"]),
        ("Drivers / locked / other", unattributed),
    ]
    for label, val in rows:
        print("  %-26s %-11s %s" % (label, fmt(val), bar(val, sys_t["physical_used"])))
    print()
    print("  %d processes, %d threads, %d handles" %
          (sys_t["process_count"], sys_t["thread_count"], sys_t["handle_count"]))
    print()

    # ---- by category -----------------------------------------------------
    cat_rss, cat_priv, cat_n = defaultdict(int), defaultdict(int), defaultdict(int)
    for d in procs.values():
        c = classify(d)
        d["cat"] = c
        cat_rss[c] += d["rss"]
        cat_priv[c] += d["private"]
        cat_n[c] += 1

    print("BY CATEGORY (working set, then commit)")
    print("  %-14s %5s %-11s %-11s %s" % ("category", "procs", "working", "commit", ""))
    for c in sorted(cat_rss, key=lambda k: -cat_rss[k]):
        print("  %-14s %5d %-11s %-11s %s" %
              (c, cat_n[c], fmt(cat_rss[c]), fmt(cat_priv[c]), bar(cat_rss[c], proc_rss, 26)))
    print()

    # ---- Directors -------------------------------------------------------
    directors = [d for d in procs.values() if d["name"].lower().startswith("cc-director")]
    print("DIRECTORS")
    for d in sorted(directors, key=lambda x: -x["rss"]):
        desc = descendants(d["pid"], kids)
        t_rss = d["rss"] + sum(procs[c]["rss"] for c in desc)
        t_priv = d["private"] + sum(procs[c]["private"] for c in desc)
        agents = [c for c in desc if procs[c]["name"].lower() == "claude.exe"]
        age_h = (time.time() - d["create_time"]) / 3600.0
        print("  %s (pid %d), up %.1f h" % (d["name"], d["pid"], age_h))
        print("    own process        %-11s working   %-11s commit" % (fmt(d["rss"]), fmt(d["private"])))
        print("    whole tree         %-11s working   %-11s commit" % (fmt(t_rss), fmt(t_priv)))
        print("    %d agent sessions, %d processes below it" % (len(agents), len(desc)))
        if agents:
            print("    per session avg    %-11s working" %
                  fmt((t_rss - d["rss"]) / len(agents)))
        print()

    # ---- sessions --------------------------------------------------------
    agents = [d for d in procs.values() if d["name"].lower() == "claude.exe"]
    if agents:
        print("AGENT SESSIONS (each agent plus everything it spawned)")
        print("  %-8s %-8s %-6s %-11s %-11s %s" %
              ("pid", "director", "age_h", "tree work", "tree commit", "children"))
        rowsum = 0
        for d in sorted(agents, key=lambda x: -(x["rss"])):
            desc = descendants(d["pid"], kids)
            t_rss = d["rss"] + sum(procs[c]["rss"] for c in desc)
            t_priv = d["private"] + sum(procs[c]["private"] for c in desc)
            rowsum += t_rss
            age_h = (time.time() - d["create_time"]) / 3600.0
            print("  %-8d %-8d %-6.1f %-11s %-11s %d" %
                  (d["pid"], d["ppid"], age_h, fmt(t_rss), fmt(t_priv), len(desc)))
        print("  %-8s %-8s %-6s %-11s" % ("TOTAL", "", "", fmt(rowsum)))
        print()

    # ---- top single processes -------------------------------------------
    print("TOP 15 SINGLE PROCESSES BY WORKING SET")
    for d in sorted(procs.values(), key=lambda x: -x["rss"])[:15]:
        print("  %-24s pid %-7d %-11s %s" % (d["name"][:24], d["pid"], fmt(d["rss"]),
                                             bar(d["rss"], proc_rss, 22)))
    print()

    # ---- reclaimable -----------------------------------------------------
    rec = find_reclaimable(procs, kids)
    rec_total = sum(d["rss"] for d, _ in rec)
    rec_priv = sum(d["private"] for d, _ in rec)
    print("RECLAIMABLE RIGHT NOW")
    if not rec:
        print("  Nothing. No orphaned or idle build processes found.")
    else:
        by_reason = defaultdict(lambda: [0, 0, 0])
        for d, reason in rec:
            e = by_reason[reason]
            e[0] += 1
            e[1] += d["rss"]
            e[2] += d["private"]
        for reason, (n, r, p) in sorted(by_reason.items(), key=lambda kv: -kv[1][1]):
            print("  %-11s %-11s  %2d procs  %s" % (fmt(r), "(%s commit)" % fmt(p), n, reason))
        print("  ---")
        print("  %-11s %-11s  %2d procs  TOTAL" % (fmt(rec_total), "(%s commit)" % fmt(rec_priv), len(rec)))
        print()
        print("  Safe way to reclaim all of it (no data loss, no running app touched):")
        print("    dotnet build-server shutdown")
        print("  That asks the build servers to exit. It does NOT touch the Director,")
        print("  the agent sessions, or any editor.")
    print()

    if html_path:
        write_html(html_path, sys_t, procs, kids, cat_rss, cat_priv, cat_n,
                   proc_rss, kernel, unattributed, rec, directors, agents)
        print("HTML map written: %s" % html_path)

    return sys_t, procs, rec


# --------------------------------------------------------------------------
# HTML map - squarified treemap, self contained, no external assets
# --------------------------------------------------------------------------

PALETTE = {
    "director": "#4c78a8", "agent": "#f58518", "build": "#e45756",
    "webview": "#72b7b2", "browser": "#54a24b", "mcp-node": "#eeca3b",
    "node": "#b279a2", "python": "#ff9da6", "dotnet-app": "#9d755d",
    "shell": "#bab0ac", "windows": "#7f8c9b", "database": "#6a5acd",
    "vm-wsl-docker": "#2f8f9d", "desktop-apps": "#a67f5d", "other": "#8c8c8c",
    "kernel": "#5c6b73", "unattributed": "#3d4a51",
}


def squarify(items, x, y, w, h):
    """Classic squarified treemap. items = [(label, value, meta), ...] desc."""
    out = []
    items = [i for i in items if i[1] > 0]
    if not items:
        return out
    total = sum(i[1] for i in items)
    if total <= 0 or w <= 0 or h <= 0:
        return out
    scale = (w * h) / total
    vals = [(i, i[1] * scale) for i in items]

    def worst(row, length):
        if not row or length <= 0:
            return float("inf")
        s = sum(r[1] for r in row)
        mx, mn = max(r[1] for r in row), min(r[1] for r in row)
        if s <= 0:
            return float("inf")
        return max((length * length * mx) / (s * s), (s * s) / (length * length * mn))

    def layout(row, x, y, w, h, horizontal):
        s = sum(r[1] for r in row)
        if s <= 0:
            return x, y, w, h
        if horizontal:
            rh = s / w if w else 0
            cx = x
            for it, v in row:
                rw = v / rh if rh else 0
                out.append((it, cx, y, rw, rh))
                cx += rw
            return x, y + rh, w, h - rh
        rw = s / h if h else 0
        cy = y
        for it, v in row:
            rh = v / rw if rw else 0
            out.append((it, x, cy, rw, rh))
            cy += rh
        return x + rw, y, w - rw, h

    row = []
    while vals:
        horizontal = w <= h
        length = w if horizontal else h
        head = vals[0]
        if not row or worst(row + [head], length) <= worst(row, length):
            row.append(head)
            vals.pop(0)
        else:
            x, y, w, h = layout(row, x, y, w, h, horizontal)
            row = []
    if row:
        layout(row, x, y, w, h, w <= h)
    return out


def write_html(path, sys_t, procs, kids, cat_rss, cat_priv, cat_n,
               proc_rss, kernel, unattributed, rec, directors, agents):
    W, H = 1160, 620
    used = sys_t["physical_used"]

    # Top level: every process grouped by category, plus the two non-process blocks.
    top = [("%s" % c, cat_rss[c], {"cat": c, "n": cat_n[c], "commit": cat_priv[c]})
           for c in cat_rss]
    top.append(("kernel pools", kernel, {"cat": "kernel", "n": 0, "commit": kernel}))
    top.append(("drivers / locked", max(unattributed, 0),
                {"cat": "unattributed", "n": 0, "commit": 0}))
    top.sort(key=lambda i: -i[1])
    tiles = squarify(top, 0, 0, W, H)

    rec_ids = {d["pid"] for d, _ in rec}
    rec_total = sum(d["rss"] for d, _ in rec)

    def esc(s):
        return html.escape(str(s))

    parts = []
    parts.append("<title>Memory map - %s</title>" % esc(os.environ.get("COMPUTERNAME", "")))
    parts.append("""<style>
:root{--bg:#ffffff;--fg:#16191d;--muted:#5b6570;--line:#e3e6ea;--card:#f7f8fa;}
@media (prefers-color-scheme:dark){:root{--bg:#14171a;--fg:#e8eaed;--muted:#9aa4ae;--line:#2a2f36;--card:#1c2025;}}
:root[data-theme="dark"]{--bg:#14171a;--fg:#e8eaed;--muted:#9aa4ae;--line:#2a2f36;--card:#1c2025;}
:root[data-theme="light"]{--bg:#ffffff;--fg:#16191d;--muted:#5b6570;--line:#e3e6ea;--card:#f7f8fa;}
body{background:var(--bg);color:var(--fg);font:15px/1.55 -apple-system,Segoe UI,Roboto,sans-serif;margin:0;padding:28px;}
h1{font-size:22px;margin:0 0 4px;} h2{font-size:16px;margin:30px 0 10px;letter-spacing:.02em;text-transform:uppercase;color:var(--muted);}
.sub{color:var(--muted);font-size:13px;margin-bottom:22px;}
.kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:26px;}
.kpi{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px;}
.kpi .v{font-size:21px;font-weight:600;} .kpi .l{color:var(--muted);font-size:12px;}
.mapwrap{overflow-x:auto;} .map{position:relative;width:%dpx;height:%dpx;border:1px solid var(--line);border-radius:8px;overflow:hidden;}
.tile{position:absolute;overflow:hidden;box-sizing:border-box;border:1px solid rgba(255,255,255,.35);color:#fff;padding:6px 8px;}
.tile b{display:block;font-size:13px;} .tile span{font-size:11px;opacity:.9;}
table{border-collapse:collapse;width:100%%;font-size:14px;} th,td{text-align:left;padding:7px 10px;border-bottom:1px solid var(--line);}
th{color:var(--muted);font-weight:600;font-size:12px;text-transform:uppercase;} td.n{text-align:right;font-variant-numeric:tabular-nums;}
.flag{color:#e45756;font-weight:600;} .ok{color:#54a24b;}
.note{background:var(--card);border-left:3px solid #4c78a8;padding:12px 14px;border-radius:6px;margin:14px 0;font-size:14px;}
code{background:var(--card);padding:2px 6px;border-radius:4px;font-size:13px;}
</style>""" % (W, H))

    parts.append("<h1>Memory map</h1>")
    parts.append('<div class="sub">%s &middot; %s &middot; %d processes, %d threads</div>' %
                 (esc(os.environ.get("COMPUTERNAME", "")), time.strftime("%Y-%m-%d %H:%M:%S"),
                  sys_t["process_count"], sys_t["thread_count"]))

    kpis = [
        ("Installed", fmt(sys_t["physical_total"])),
        ("Physical in use", fmt(used)),
        ("Available", fmt(sys_t["physical_available"])),
        ("Committed", fmt(sys_t["commit_total"])),
        ("Commit limit", fmt(sys_t["commit_limit"])),
        ("Reclaimable now", fmt(rec_total)),
    ]
    parts.append('<div class="kpis">')
    for l, v in kpis:
        parts.append('<div class="kpi"><div class="v">%s</div><div class="l">%s</div></div>' % (esc(v), esc(l)))
    parts.append("</div>")

    parts.append("<h2>Physical memory, by owner</h2>")
    parts.append('<div class="mapwrap"><div class="map">')
    for (label, val, meta), x, y, w, h in tiles:
        if w < 1 or h < 1:
            continue
        color = PALETTE.get(meta["cat"], "#8c8c8c")
        show = w > 62 and h > 26
        inner = ""
        if show:
            inner = "<b>%s</b><span>%s%s</span>" % (
                esc(label), esc(fmt(val)),
                (" &middot; %d procs" % meta["n"]) if meta["n"] else "")
        parts.append('<div class="tile" style="left:%.1fpx;top:%.1fpx;width:%.1fpx;height:%.1fpx;background:%s" title="%s - %s">%s</div>'
                     % (x, y, w, h, color, esc(label), esc(fmt(val)), inner))
    parts.append("</div></div>")

    parts.append("<h2>Directors</h2><table><tr><th>process</th><th>up</th><th class='n'>own working set</th><th class='n'>own commit</th><th class='n'>whole tree</th><th class='n'>sessions</th><th class='n'>procs below</th></tr>")
    for d in sorted(directors, key=lambda x: -x["rss"]):
        desc = descendants(d["pid"], kids)
        t_rss = d["rss"] + sum(procs[c]["rss"] for c in desc)
        n_ag = len([c for c in desc if procs[c]["name"].lower() == "claude.exe"])
        age_h = (time.time() - d["create_time"]) / 3600.0
        flag = ' class="flag"' if d["rss"] > 2 * GB else ""
        parts.append("<tr><td>%s <span style='color:var(--muted)'>pid %d</span></td><td>%.1f h</td><td class='n'%s>%s</td><td class='n'>%s</td><td class='n'>%s</td><td class='n'>%d</td><td class='n'>%d</td></tr>"
                     % (esc(d["name"]), d["pid"], age_h, flag, esc(fmt(d["rss"])),
                        esc(fmt(d["private"])), esc(fmt(t_rss)), n_ag, len(desc)))
    parts.append("</table>")

    parts.append("<h2>Agent sessions</h2><table><tr><th>agent pid</th><th>director</th><th>age</th><th class='n'>tree working set</th><th class='n'>tree commit</th><th class='n'>child procs</th></tr>")
    for d in sorted(agents, key=lambda x: -x["rss"]):
        desc = descendants(d["pid"], kids)
        t_rss = d["rss"] + sum(procs[c]["rss"] for c in desc)
        t_priv = d["private"] + sum(procs[c]["private"] for c in desc)
        age_h = (time.time() - d["create_time"]) / 3600.0
        parts.append("<tr><td>%d</td><td>%d</td><td>%.1f h</td><td class='n'>%s</td><td class='n'>%s</td><td class='n'>%d</td></tr>"
                     % (d["pid"], d["ppid"], age_h, esc(fmt(t_rss)), esc(fmt(t_priv)), len(desc)))
    parts.append("</table>")

    parts.append("<h2>Reclaimable right now</h2>")
    if not rec:
        parts.append('<div class="note ok">Nothing reclaimable - no orphaned or idle build processes.</div>')
    else:
        by_reason = defaultdict(lambda: [0, 0])
        for d, reason in rec:
            by_reason[reason][0] += 1
            by_reason[reason][1] += d["rss"]
        parts.append("<table><tr><th>reason</th><th class='n'>processes</th><th class='n'>working set</th></tr>")
        for reason, (n, r) in sorted(by_reason.items(), key=lambda kv: -kv[1][1]):
            parts.append("<tr><td>%s</td><td class='n'>%d</td><td class='n'>%s</td></tr>" % (esc(reason), n, esc(fmt(r))))
        parts.append("<tr><td><b>Total</b></td><td class='n'><b>%d</b></td><td class='n'><b>%s</b></td></tr></table>" % (len(rec), esc(fmt(rec_total))))
        parts.append('<div class="note">Reclaim all of it without touching the Director, the agent sessions, or any editor: <code>dotnet build-server shutdown</code></div>')

    parts.append("<h2>Top processes</h2><table><tr><th>process</th><th>pid</th><th>category</th><th class='n'>working set</th><th class='n'>commit</th></tr>")
    for d in sorted(procs.values(), key=lambda x: -x["rss"])[:25]:
        mark = ' <span class="flag">reclaimable</span>' if d["pid"] in rec_ids else ""
        parts.append("<tr><td>%s%s</td><td>%d</td><td>%s</td><td class='n'>%s</td><td class='n'>%s</td></tr>"
                     % (esc(d["name"]), mark, d["pid"], esc(d.get("cat", "")), esc(fmt(d["rss"])), esc(fmt(d["private"]))))
    parts.append("</table>")

    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(parts))


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--html", help="also write an HTML memory map to this path")
    a = ap.parse_args()
    build_report(a.html)
