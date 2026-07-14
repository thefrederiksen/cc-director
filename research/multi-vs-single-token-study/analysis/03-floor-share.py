# Floor-reload share of total cost.
# floor-reload = turns * ~69,000 (the fixed per-session start floor: base system prompt +
# built-in tool schemas + global/project CLAUDE.md + MEMORY.md + skills list + deferred MCP
# tool names + fleet preamble). That floor is re-read on every turn as cache_read, so its
# lifetime contribution is turns * floor. This script shows how much of each run's total
# token footprint is JUST that fixed floor being re-read.

def f(n): return f"{n:,}"
FLOOR = 69000  # measured start floor, ~identical across all 6 sessions

# (label, [(session, turns, total_tokens), ...])
runs = {
    "SINGLE (1 session)":             [("single", 64, 6670262)],
    "MULTI all 5":                    [("arch", 114, 12177272), ("mgr", 92, 8916087),
                                       ("w1", 23, 1776644), ("w2", 25, 2104098), ("w3", 26, 2121049)],
    "MULTI builders (mgr+3 workers)": [("mgr", 92, 8916087), ("w1", 23, 1776644),
                                       ("w2", 25, 2104098), ("w3", 26, 2121049)],
}

print(f"{'run':<34}{'turns':>7}{'total tok':>13}{'floor-reload':>14}{'floor %':>9}")
for name, sess in runs.items():
    turns = sum(s[1] for s in sess)
    total = sum(s[2] for s in sess)
    floor = turns * FLOOR
    print(f"{name:<34}{turns:>7}{f(total):>13}{f(floor):>14}{100*floor/total:>8.0f}%")
