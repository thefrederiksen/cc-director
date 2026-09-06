import json, datetime as dt, sys, os
from collections import Counter, defaultdict
from zoneinfo import ZoneInfo
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pg
ROOT = r"D:/Personal/OneDrive/Center Consulting/DevThrottle/mentor-data/accounts/soren/raw/db"
TZ = ZoneInfo("America/Toronto")
START = dt.datetime(2026,8,24,tzinfo=TZ); END = dt.datetime(2026,8,31,tzinfo=TZ)
def parse(s): return dt.datetime.fromisoformat(s.replace("Z","+00:00"))

ev=[]
with open(ROOT+"/activity_events.jsonl", encoding="utf-8") as f:
    for line in f:
        r=json.loads(line)
        if r.get("EventType")!="turn-submitted": continue
        ts=parse(r["OccurredUtc"])
        if START <= ts < END: r["_ts"]=ts; ev.append(r)

# sessions
started={}
with open(ROOT+"/session_history.jsonl", encoding="utf-8") as f:
    for line in f:
        r=json.loads(line)
        started[r["SessionId"]] = parse(r["StartedAtUtc"]) if r.get("StartedAtUtc") else None

fw=[r for r in ev if r.get("SendSource")=="Framework"]
lags=[]
for r in fw:
    st=started.get(r["SessionId"])
    if st: lags.append((r["_ts"]-st).total_seconds())
lags.sort()
print("Framework turn-submitted events:", len(fw), " with a known session start:", len(lags))
if lags:
    import statistics
    print("  seconds from session start: min %.1f p50 %.1f p90 %.1f max %.1f" % (lags[0], lags[len(lags)//2], lags[int(len(lags)*0.9)], lags[-1]))
    print("  within 120s of session start:", sum(1 for x in lags if x<=120), "of", len(lags))
print()
# sessions with origin-carrying events, absent from stat_delta
cols, rows = pg.q("""select distinct session_id from gateway_stats.stat_delta
  where tenant=%s and hour_utc >= %s and hour_utc < %s""",
  ('9f19679f-2e19-41a7-9acf-8cae7a8a59cc','2026-08-24T04','2026-08-31T04'))
thr = {r[0] for r in rows}
by_ses = defaultdict(Counter)
for r in ev: by_ses[r["SessionId"]][(r.get("SendSource"), r.get("InputOrigin"))] += 1
missing = [s for s in by_ses if s not in thr]
print("ledger sessions:", len(by_ses), " in throttle store:", len(thr), " ledger-only:", len(missing))
kinds=Counter()
for s in missing:
    c=by_ses[s]
    has_ui_origin = any(k[0]=="UserInput" and k[1] for k in c)
    has_null_src  = any(k[0] is None for k in c)
    has_delivery  = any(k[0]=="Delivery" for k in c)
    only_fw       = all(k[0]=="Framework" for k in c)
    kinds[("ui+origin" if has_ui_origin else "") + ("|null-src(terminal typing)" if has_null_src else "") + ("|delivery" if has_delivery else "") + ("|framework-only" if only_fw else "")] += 1
for k,v in kinds.most_common(): print("   %-45s %d" % (k or "(other)", v))
print()
lost = sum(v for s in missing for k,v in by_ses[s].items() if k[1])
print("origin-carrying turn events in ledger-only sessions:", lost)
