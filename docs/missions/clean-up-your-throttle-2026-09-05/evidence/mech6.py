"""Does the store's OWN count of adopted resets (the generation column) match the number of
restatements the walk finds? Generation is only ever raised by the adopting-a-reset branch, and that
branch is the only one that lowers a metric - which is the only thing besides a fresh insert that makes
the writer append a whole cumulative. So the two numbers should agree, bucket for bucket, if the reset
branch is the mechanism."""
import sys, os
from collections import Counter, defaultdict
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pg

TENANT = '9f19679f-2e19-41a7-9acf-8cae7a8a59cc'
# Buckets whose high-water row still exists: those are the only ones whose generation we can read.
cols, hw = pg.q("""select session_id, modality, surface, turns, chars, generation
                   from gateway_stats.session_highwater where tenant=%s""", (TENANT,))
live = {(s, m, su): (int(t), int(c), int(g)) for s, m, su, t, c, g in hw}
sessions = sorted({k[0] for k in live})
cols, rows = pg.q("""select id, session_id, modality, surface, hour_utc, turns, chars
                     from gateway_stats.stat_delta
                     where tenant=%s and session_id = any(%s) order by id""", (TENANT, sessions))
buckets = defaultdict(list)
for i, s, m, su, h, t, c in rows:
    buckets[(s, m, su)].append((i, h, int(t), int(c)))

agree = disagree = 0
pairs = Counter()
for key, rs in buckets.items():
    if key not in live: continue
    gen = live[key][2]
    T = C = 0; cumset = {(0, 0)}; seen = set(); n = 0
    for i, h, t, c in rs:
        if (t, c) in cumset and (t, c) != (0, 0): n += 1; continue
        if (h, t, c) in seen: continue
        seen.add((h, t, c)); T += t; C += c; cumset.add((T, C))
    pairs[(gen, n)] += 1
    if gen == n: agree += 1
    else: disagree += 1

print("buckets with a surviving high-water row and a delta history: %d" % (agree + disagree))
print("  generation EQUALS the restatement count: %d" % agree)
print("  they differ:                             %d" % disagree)
print()
print("(generation, restatements) -> how many buckets:")
for k, v in sorted(pairs.items())[:30]:
    print("   gen=%-3d restatements=%-3d  %4d buckets" % (k[0], k[1], v))
print()
# The zero-generation buckets that nonetheless restated are the ones the reset branch cannot explain.
zero_gen_restated = sum(v for k, v in pairs.items() if k[0] == 0 and k[1] > 0)
zero_gen_clean = sum(v for k, v in pairs.items() if k[0] == 0 and k[1] == 0)
nonzero_gen = sum(v for k, v in pairs.items() if k[0] > 0)
print("buckets at generation 0 that restated at least once: %d" % zero_gen_restated)
print("buckets at generation 0 that never restated:         %d" % zero_gen_clean)
print("buckets that ever adopted a reset (generation > 0):   %d" % nonzero_gen)
