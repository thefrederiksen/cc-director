import json, datetime as dt
from collections import Counter
from zoneinfo import ZoneInfo
ROOT = r"D:/Personal/OneDrive/Center Consulting/DevThrottle/mentor-data/accounts/soren/raw/db"
TZ = ZoneInfo("America/Toronto")
START = dt.datetime(2026,8,24,tzinfo=TZ); END = dt.datetime(2026,8,31,tzinfo=TZ)
def parse(s): return dt.datetime.fromisoformat(s.replace("Z","+00:00"))
rows=[]
with open(ROOT+"/activity_events.jsonl", encoding="utf-8") as f:
    for line in f:
        r=json.loads(line)
        if r.get("EventType")!="turn-submitted": continue
        ts=parse(r["OccurredUtc"])
        if START <= ts < END: rows.append(r)
none_src=[r for r in rows if r.get("SendSource") is None]
print("SendSource null:", len(none_src))
print("  Cause:", dict(Counter(r.get("Cause") for r in none_src)))
print("  InputOrigin:", dict(Counter(r.get("InputOrigin") for r in none_src)))
print("  earliest:", min(r["OccurredUtc"] for r in none_src), " latest:", max(r["OccurredUtc"] for r in none_src))
print("  machines:", dict(Counter(r.get("Machine") for r in none_src)))
print()
ui_noorigin=[r for r in rows if r.get("SendSource")=="UserInput" and not r.get("InputOrigin")]
print("UserInput with NO InputOrigin (product gap 2639 candidates):", len(ui_noorigin))
print("  machines:", dict(Counter(r.get("Machine") for r in ui_noorigin)))
print("  agents:", dict(Counter(r.get("AgentKind") for r in ui_noorigin)))
print("  distinct sessions:", len({r["SessionId"] for r in ui_noorigin}))
print()
fw=[r for r in rows if r.get("SendSource")=="Framework"]
print("Framework (chat relay + handovers):", len(fw), "sessions:", len({r["SessionId"] for r in fw}))
print("  machines:", dict(Counter(r.get("Machine") for r in fw)))
print()
# by machine, the modality split of origin-carrying events
for m in ("SOREN_NORTH","DEVTHROTTLE_2"):
    sub=[r for r in rows if r.get("Machine")==m and r.get("InputOrigin")]
    c=Counter(r["InputOrigin"] for r in sub)
    print(m, "origin-carrying:", len(sub), dict(c))
