"""TODAY's restatements, with the generation the bucket is on right now. A restatement on a bucket whose
generation is still 0 cannot be an adopted reset - that branch is the only thing that raises generation -
so its high-water row must have been ABSENT when the delta was written."""
import sys, os
from collections import defaultdict
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pg
TENANT = '9f19679f-2e19-41a7-9acf-8cae7a8a59cc'
cols, rows = pg.q("""select id, session_id, modality, surface, hour_utc, turns, chars
                     from gateway_stats.stat_delta
                     where tenant=%s and hour_utc >= '2026-09-01T00' order by id""", (TENANT,))
buckets = defaultdict(list)
for i, s, m, su, h, t, c in rows: buckets[(s, m, su)].append((i, h, int(t), int(c)))
cols, hw = pg.q("""select session_id, modality, surface, turns, chars, generation
                   from gateway_stats.session_highwater where tenant=%s""", (TENANT,))
live = {(s, m, su): (int(t), int(c), int(g)) for s, m, su, t, c, g in hw}
found = []
for key, rs in buckets.items():
    T = C = 0; cumset = {(0, 0)}; seen = set()
    for i, h, t, c in rs:
        if (t, c) in cumset and (t, c) != (0, 0):
            found.append((i, h, key, t, c, T, C, live.get(key))); continue
        if (h, t, c) in seen: continue
        seen.add((h, t, c)); T += t; C += c; cumset.add((T, C))
print("restatements since 1 September: %d" % len(found))
print("%-6s %-14s %-10s %-8s %-6s %-14s %s" % ("id","hour","modality","surface","re-add","truth then","high-water now (turns,chars,GEN)"))
for i, h, key, t, c, T, C, hwv in found[-25:]:
    print("%-6d %-14s %-10s %-8s %-6s %-14s %s" % (i, h, key[1], key[2], "%d/%d" % (t, c), "%d/%d" % (T, C), hwv))
gen0 = [f for f in found if f[7] is not None and f[7][2] == 0]
gone = [f for f in found if f[7] is None]
print()
print("restatements whose bucket's high-water row still exists and is at GENERATION 0: %d" % len(gen0))
print("restatements whose bucket's high-water row is gone entirely:                    %d" % len(gone))
print("restatements whose bucket has adopted at least one reset:                       %d"
      % len([f for f in found if f[7] is not None and f[7][2] > 0]))
