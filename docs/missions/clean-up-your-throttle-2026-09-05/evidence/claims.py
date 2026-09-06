import sys, os, datetime as dt
from collections import Counter
MENTOR = r"D:/ReposFred/devthrottle_internal-throttle/tools/mentor"
sys.path.insert(0, MENTOR); os.chdir(MENTOR)
import metrics, origin as origin_module
from pathlib import Path
ROOT = Path(r"D:/Personal/OneDrive/Center Consulting/DevThrottle/mentor-data/accounts/soren")
paths = {"prompt_log": ROOT/"raw"/"prompt-log", "db": ROOT/"raw"/"db"}
ends = {s: dt.datetime(2026,9,1,22,27,6, tzinfo=dt.timezone.utc) for s in metrics.SOURCE_NAMES}
world = metrics.load_world("soren", "America/Toronto", paths, ends)
week = metrics.Week(world, "2026-W35", {"start":8,"end":18})
print("rule counts over the week's user records:")
for k,v in sorted(Counter((p["origin"], p["origin_rule"]) for p in week.user_prompts).items(), key=lambda x:-x[1]):
    print("   %-12s %-42s %5d" % (k[0], k[1], v))
print()
# how many human prompts came from a stamp vs from the ledger
print("human by rule:", dict(Counter(p["origin_rule"] for p in week.human_prompts)))
# what the ledger-origin humans looked like
lo=[p for p in week.human_prompts if p["origin_rule"]=="ledger-origin"]
print("ledger-origin humans by modality/surface:", dict(Counter((p["origin_modality"],p["origin_surface"]) for p in lo)))
st=[p for p in week.human_prompts if p["origin_rule"]=="stamped"]
print("stamped humans by modality/surface:", dict(Counter((p["origin_modality"],p["origin_surface"]) for p in st)))
