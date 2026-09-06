"""Is the restatement scattered per session, or clustered on fleet-wide moments?"""
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

restatement_rows = []   # (id, hour, session, modality, surface, turns, chars)
duplicate_rows = []
for (s, m, su), rs in buckets.items():
    T = C = 0; cumset = {(0, 0)}; seen = set()
    for i, h, t, c in rs:
        if (t, c) in cumset and (t, c) != (0, 0):
            restatement_rows.append((i, h, s, m, su, t, c)); continue
        if (h, t, c) in seen:
            duplicate_rows.append((i, h, s, m, su, t, c)); continue
        seen.add((h, t, c)); T += t; C += c; cumset.add((T, C))

print("restatement rows: %d over %d distinct hours" % (len(restatement_rows), len({r[1] for r in restatement_rows})))
byhour = Counter(r[1] for r in restatement_rows)
print("the hours carrying the most restatements:")
for h, n in byhour.most_common(15):
    sess = len({r[2] for r in restatement_rows if r[1] == h})
    print("   %s  %3d restatements over %2d distinct sessions" % (h, n, sess))
print()

# The decisive question for H1: are the restatements ADJACENT in row id to each other across DIFFERENT
# sessions? A fleet-wide re-fold writes one burst; per-session removes write scattered singletons.
ids = sorted(r[0] for r in restatement_rows)
allids = sorted(r[0] for r in rows)
pos = {i: n for n, i in enumerate(allids)}
runs = []
cur = [ids[0]]
for a, b in zip(ids, ids[1:]):
    if pos[b] - pos[pos_key] if False else False: pass
    if pos[b] - pos[a] <= 2:
        cur.append(b)
    else:
        runs.append(cur); cur = [b]
runs.append(cur)
runs.sort(key=len, reverse=True)
print("restatements arrive in %d runs of rows that are within 2 delta rows of each other." % len(runs))
print("the largest runs (length, distinct sessions in the run, hour):")
byid = {r[0]: r for r in restatement_rows}
for run in runs[:12]:
    ss = {byid[i][2] for i in run}
    print("   %3d rows, %2d sessions, %s .. %s" % (len(run), len(ss), byid[run[0]][1], byid[run[-1]][1]))
print("runs of length 1: %d" % sum(1 for r in runs if len(r) == 1))
print()

# And the same for duplicates.
print("duplicate rows: %d" % len(duplicate_rows))
dids = sorted(r[0] for r in duplicate_rows)
adj = sum(1 for i in dids if (i - 1) in {r[0] for r in restatement_rows} or (i - 2) in {r[0] for r in restatement_rows})
print("duplicates whose row id sits within 2 of a restatement: %d (%.0f%%)" % (adj, 100.0 * adj / len(dids)))
