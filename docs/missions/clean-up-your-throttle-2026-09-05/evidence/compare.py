import json, datetime as dt, sys, os
from collections import Counter, defaultdict
from zoneinfo import ZoneInfo
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pg
ROOT = r"D:/Personal/OneDrive/Center Consulting/DevThrottle/mentor-data/accounts/soren/raw/db"
TZ = ZoneInfo("America/Toronto")
START = dt.datetime(2026,8,24,tzinfo=TZ); END = dt.datetime(2026,8,31,tzinfo=TZ)
def parse(s): return dt.datetime.fromisoformat(s.replace("Z","+00:00"))

# ledger
led = defaultdict(Counter)   # session -> Counter(origin string / 'no-origin')
with open(ROOT+"/activity_events.jsonl", encoding="utf-8") as f:
    for line in f:
        r=json.loads(line)
        if r.get("EventType")!="turn-submitted": continue
        ts=parse(r["OccurredUtc"])
        if not (START <= ts < END): continue
        led[r["SessionId"]][r.get("InputOrigin") or ("no-origin/"+str(r.get("SendSource")))] += 1

# throttle
cols, rows = pg.q("""select session_id, modality, surface, sum(turns) from gateway_stats.stat_delta
 where tenant=%s and hour_utc >= %s and hour_utc < %s group by 1,2,3""",
 ('9f19679f-2e19-41a7-9acf-8cae7a8a59cc','2026-08-24T04','2026-08-31T04'))
thr = defaultdict(Counter)
for s,m,su,t in rows: thr[s][m+"/"+su] += t

allses = set(led) | set(thr)
print("sessions with ledger turn events :", len(led))
print("sessions with throttle stat rows :", len(thr))
print("in both                          :", len(set(led)&set(thr)))
print("ledger only                      :", len(set(led)-set(thr)))
print("throttle only                    :", len(set(thr)-set(led)))
print()
def tot(c, pred=lambda k: True): return sum(v for k,v in c.items() if pred(k))
lt = sum(tot(c, lambda k: not k.startswith("no-origin")) for c in led.values())
tt = sum(tot(c) for c in thr.values())
print("ledger origin-carrying turns:", lt, " throttle turns:", tt)
print()
print("Top 15 sessions by throttle turns, against the ledger:")
print("session                                  thr    led  ratio  throttle split | ledger split")
for s,_ in sorted(((s, tot(thr[s])) for s in thr), key=lambda x:-x[1])[:15]:
    l = led.get(s, Counter())
    lo = tot(l, lambda k: not k.startswith("no-origin"))
    ratio = ("%.2f" % (tot(thr[s])/lo)) if lo else "-"
    print("%s %6d %6d %6s  %s | %s" % (s, tot(thr[s]), lo, ratio, dict(thr[s]), dict(l)))
