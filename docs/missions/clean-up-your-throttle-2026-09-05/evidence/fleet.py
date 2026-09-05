import sys, os, datetime as dt
from collections import Counter
MENTOR = r"D:/ReposFred/devthrottle_internal-throttle/tools/mentor"
sys.path.insert(0, MENTOR); os.chdir(MENTOR)
import metrics, origin as origin_module
from pathlib import Path
ROOT = Path(r"D:/Personal/OneDrive/Center Consulting/DevThrottle/mentor-data/accounts/soren")
ends = {s: dt.datetime(2026,9,1,22,27,6, tzinfo=dt.timezone.utc) for s in metrics.SOURCE_NAMES}
world = metrics.load_world("soren","America/Toronto",
        {"prompt_log": ROOT/"raw"/"prompt-log", "db": ROOT/"raw"/"db"}, ends)
week = metrics.Week(world, "2026-W35", {"start":8,"end":18})
fleet = [p for p in week.user_prompts if p.get("origin_rule","").startswith("envelope")]
# Independently: for each fleet-message record find the nearest turn-submitted event within 23s
led = origin_module.Ledger(world.events)
hit = Counter(); 
for p in fleet:
    rows = led.by_session.get(p["session"], [])
    best=None; bd=None
    for r in rows:
        d=abs((r["ts"]-p["ts"]).total_seconds())
        if d<=origin_module.LEDGER_JOIN_SECONDS and (bd is None or d<bd): best,bd=r,d
    if best is None: hit["no event within 23s"]+=1
    else: hit[(best["source"], best["origin"][0] if best["origin"] else None)]+=1
print("fleet-message records in W35:", len(fleet))
print("nearest turn-submitted event for each:")
for k,v in sorted(hit.items(), key=lambda x:-x[1]): print("   %-32s %d" % (str(k), v))
