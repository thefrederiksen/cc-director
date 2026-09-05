"""Count only the restatements that CARRY TURNS, and compare them with the store's own reset counter.

A row of 0 turns and a few characters cannot restate a turn tally, and phase one's walk classified some
of those as restatements because (0, n) had been reached before. They contribute nothing to the inflated
turn count, so they are separated out here rather than argued about."""
import sys, os
from collections import Counter, defaultdict
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pg
TENANT = '9f19679f-2e19-41a7-9acf-8cae7a8a59cc'
cols, hw = pg.q("""select session_id, modality, surface, generation from gateway_stats.session_highwater
                   where tenant=%s""", (TENANT,))
live = {(s, m, su): int(g) for s, m, su, g in hw}
sessions = sorted({k[0] for k in live})
cols, rows = pg.q("""select id, session_id, modality, surface, hour_utc, turns, chars
                     from gateway_stats.stat_delta where tenant=%s and session_id = any(%s) order by id""",
                  (TENANT, sessions))
buckets = defaultdict(list)
for i, s, m, su, h, t, c in rows: buckets[(s, m, su)].append((i, h, int(t), int(c)))

pairs = Counter(); zero_turn = 0; with_turn = 0
for key, rs in buckets.items():
    if key not in live: continue
    T = C = 0; cumset = {(0, 0)}; seen = set(); n = 0
    for i, h, t, c in rs:
        if (t, c) in cumset and (t, c) != (0, 0):
            if t > 0: n += 1; with_turn += 1
            else: zero_turn += 1
            continue
        if (h, t, c) in seen: continue
        seen.add((h, t, c)); T += t; C += c; cumset.add((T, C))
    pairs[(live[key], n)] += 1

print("restatement rows carrying at least one turn: %d" % with_turn)
print("restatement rows carrying ZERO turns (character-only, not a turn restatement): %d" % zero_turn)
print()
agree = sum(v for k, v in pairs.items() if k[0] == k[1])
print("buckets where the store's own reset count EQUALS the turn-carrying restatements: %d of %d"
      % (agree, sum(pairs.values())))
print()
print("(generation, turn-carrying restatements) -> buckets:")
for k, v in sorted(pairs.items()):
    mark = "" if k[0] == k[1] else "   <-- differ"
    print("   gen=%-3d restatements=%-3d  %4d buckets%s" % (k[0], k[1], v, mark))
