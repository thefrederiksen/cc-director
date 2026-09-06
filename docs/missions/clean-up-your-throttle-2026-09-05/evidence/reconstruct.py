import sys, os
from collections import Counter, defaultdict
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pg

TENANT = '9f19679f-2e19-41a7-9acf-8cae7a8a59cc'
FROM, TO = '2026-08-24T04', '2026-08-31T04'

cols, rows = pg.q("""select id, session_id, modality, surface, hour_utc, turns, chars
                     from gateway_stats.stat_delta
                     where tenant=%s and hour_utc >= %s and hour_utc < %s
                     order by id""", (TENANT, FROM, TO))
buckets = defaultdict(list)
for i, s, m, su, h, t, c in rows:
    buckets[(s, m, su)].append((i, h, int(t), int(c)))

stored = Counter(); true = Counter(); restated = Counter(); dupd = Counter()
rowclass = Counter()
suspect = []
true_by_session = defaultdict(Counter)
for (s, m, su), rs in buckets.items():
    T = C = 0
    cumset = {(0, 0)}
    seen = set()
    for i, h, t, c in rs:
        stored[(m, su)] += t
        if (t, c) in cumset and (t, c) != (0, 0):
            restated[(m, su)] += t; rowclass["restatement"] += 1; continue
        if (h, t, c) in seen:
            dupd[(m, su)] += t; rowclass["duplicate"] += 1; continue
        seen.add((h, t, c))
        T += t; C += c; cumset.add((T, C))
        rowclass["accepted"] += 1
        if t > 1:
            suspect.append((s, m, su, i, h, t, c))
    true[(m, su)] += T
    true_by_session[s][(m, su)] += T

def show(name, ctr):
    tot = sum(ctr.values())
    v = sum(v for k, v in ctr.items() if k[0] == 'voice')
    print("%-12s total=%5d voice=%5d typed=%5d voice_share=%s" %
          (name, tot, v, tot - v, ("%.4f" % (v / tot)) if tot else "-"))
    for k in sorted(ctr, key=lambda k: -ctr[k]):
        print("               %-6s %-8s %6d" % (k[0], k[1], ctr[k]))

show("STORED", stored); print()
show("RECONSTRUCTED", true); print()
show("RESTATED", restated); print()
show("DUPLICATED", dupd); print()
print("row classes:", dict(rowclass))
print("accepted rows carrying turns>1 (would be an unmatched restatement):", len(suspect))
for r in suspect[:15]: print("   ", r)
import json
json.dump({("%s|%s|%s" % (s, k[0], k[1])): v
           for s, cc in true_by_session.items() for k, v in cc.items()},
          # written to the current working directory, not into the repository
          open("true_by_session.json", "w"))
