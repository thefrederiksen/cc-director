import json, datetime as dt
from collections import Counter, defaultdict
from zoneinfo import ZoneInfo

ROOT = r"D:/Personal/OneDrive/Center Consulting/DevThrottle/mentor-data/accounts/soren/raw/db"
TZ = ZoneInfo("America/Toronto")
START = dt.datetime(2026,8,24,tzinfo=TZ); END = dt.datetime(2026,8,31,tzinfo=TZ)

def parse(s):
    return dt.datetime.fromisoformat(s.replace("Z","+00:00"))

rows=[]; machines=Counter(); directors=Counter()
with open(ROOT+"/activity_events.jsonl", encoding="utf-8") as f:
    for line in f:
        r=json.loads(line)
        if r.get("EventType")!="turn-submitted": continue
        ts=parse(r["OccurredUtc"])
        if not (START <= ts < END): continue
        rows.append(r); machines[r.get("Machine")]+=1; directors[r.get("DirectorId")]+=1

print("turn-submitted events in W35 (Toronto week):", len(rows))
print("by SendSource:", dict(Counter(r.get("SendSource") for r in rows)))
print("by Cause:", dict(Counter(r.get("Cause") for r in rows)))
print("machines:", dict(machines))
print("directors:", len(directors), dict(list(directors.items())[:10]))
print()
ui=[r for r in rows if r.get("SendSource")=="UserInput"]
print("UserInput events:", len(ui))
print("  InputOrigin split:", dict(Counter(r.get("InputOrigin") for r in ui)))
dl=[r for r in rows if r.get("SendSource")=="Delivery"]
print("Delivery events:", len(dl), dict(Counter(r.get("InputOrigin") for r in dl)))
fw=[r for r in rows if r.get("SendSource")=="Framework"]
print("Framework events:", len(fw), dict(Counter(r.get("InputOrigin") for r in fw)))
ag=[r for r in rows if r.get("SendSource")=="Agent"]
print("Agent events:", len(ag), dict(Counter(r.get("InputOrigin") for r in ag)))
print()
# modality view over every event carrying an origin
withorigin=[r for r in rows if r.get("InputOrigin")]
mod=Counter(r["InputOrigin"].split("/")[0] for r in withorigin)
surf=Counter(r["InputOrigin"].split("/")[1] for r in withorigin)
print("events carrying an InputOrigin:", len(withorigin), "modality:", dict(mod), "surface:", dict(surf))
tot=sum(mod.values())
print("  voice share of origin-carrying events: %.4f" % (mod["voice"]/tot))
print()
print("distinct sessions with a turn-submitted event:", len({r["SessionId"] for r in rows}))
print("distinct sessions with a UserInput event:", len({r["SessionId"] for r in ui}))
