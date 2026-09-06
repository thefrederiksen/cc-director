import sys, os, json, datetime as dt
from collections import Counter, defaultdict
MENTOR = r"D:/ReposFred/devthrottle_internal-throttle/tools/mentor"
sys.path.insert(0, MENTOR)
os.chdir(MENTOR)
import metrics, origin as origin_module
from pathlib import Path
from zoneinfo import ZoneInfo

ROOT = Path(r"D:/Personal/OneDrive/Center Consulting/DevThrottle/mentor-data/accounts/soren")
paths = {"prompt_log": ROOT/"raw"/"prompt-log", "db": ROOT/"raw"/"db"}
# extract times: read from the manifest the same way metrics.py does is heavy; use generous ends.
ends = {s: dt.datetime(2026,9,1,22,27,6, tzinfo=dt.timezone.utc) for s in metrics.SOURCE_NAMES}
world = metrics.load_world("soren", "America/Toronto", paths, ends)
week = metrics.Week(world, "2026-W35", {"start":8,"end":18})
print("week bounds:", week.start, "->", week.end)
print("user prompts in week:", len(week.user_prompts))
print("human prompts:", len(week.human_prompts))
print("origin classes:", dict(Counter(p["origin"] for p in week.user_prompts)))
hm = Counter(p["origin_modality"] for p in week.human_prompts)
hs = Counter(p["origin_surface"] for p in week.human_prompts)
print("human modality:", dict(hm), " voice share = %.4f" % (hm["voice"]/len(week.human_prompts)))
print("human surface:", dict(hs))
print("human modality x surface:", dict(Counter((p["origin_modality"],p["origin_surface"]) for p in week.human_prompts)))
print()
# fleet messages that carried a human stamp
env = [p for p in week.user_prompts if p.get("origin_rule","" ).startswith("envelope")]
print("fleet-message envelopes in week:", len(env),
      " of which carried a human stamp:", sum(1 for p in env if p.get("origin_rule","").startswith("envelope-over-stamp")))
print()
# ledger claim accounting over the week
led = origin_module.Ledger(world.events)
inweek = [r for rows in led.by_session.values() for r in rows if week.start <= r["ts"] < week.end]
print("turn-submitted events in week:", len(inweek))
print("  carrying an origin:", sum(1 for r in inweek if r["origin"]))
by = Counter((r["source"], (r["origin"][0] if r["origin"] else None)) for r in inweek)
for k,v in sorted(by.items(), key=lambda x:-x[1]): print("   %-30s %d" % (str(k), v))
print()
json.dump({"human_by_session": dict(Counter(p["session"] for p in week.human_prompts)),
           "human_by_session_modality": {k[0]+"|"+k[1]: v for k,v in
               Counter((p["session"], p["origin_modality"]) for p in week.human_prompts).items()}},
          open(os.path.join(os.environ.get("TEMP","."), "mentor_by_session.json"), "w"))
print("sessions with at least one human prompt:", len({p["session"] for p in week.human_prompts}))
