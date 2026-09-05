"""Phase two, task 3 - PROVE the mechanism behind defect two before changing anything.

Reads only. Two rival explanations for the restated cumulatives phase one measured:

  H1  the high-water ROW WAS DELETED and re-inserted. A fresh insert seeds previous_* to zero, so the
      whole standing cumulative is appended as one delta. Generation starts again at 0.
  H2  a RESET WAS ADOPTED - the Director restarted the session and reported a lower count, so the store
      took the drop as real and counted from zero again. The row's generation ADVANCES (+1 per reset).

They are distinguishable in the stored rows: generation is the store's own count of how many resets it
adopted for that row, and it is only ever raised by the adopting-a-reset branch.
"""
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

# Re-run phase one's walk, and keep WHICH buckets restated and how many times.
restating = Counter()
duplicating = Counter()
accepted_rows = Counter()
first_row_turns = {}
for key, rs in buckets.items():
    T = C = 0
    cumset = {(0, 0)}
    seen = set()
    first_row_turns[key] = rs[0][2]
    for i, h, t, c in rs:
        if (t, c) in cumset and (t, c) != (0, 0):
            restating[key] += 1; continue
        if (h, t, c) in seen:
            duplicating[key] += 1; continue
        seen.add((h, t, c)); T += t; C += c; cumset.add((T, C)); accepted_rows[key] += 1

print("session buckets with a delta row this week: %d" % len(buckets))
print("  of which restated at least once: %d  (%d restatement rows)"
      % (len(restating), sum(restating.values())))
print("  of which duplicated at least once: %d  (%d duplicate rows)"
      % (len(duplicating), sum(duplicating.values())))
print()

# The current high-water rows for those sessions.
sessions = sorted({k[0] for k in buckets})
cols, hw = pg.q("""select session_id, modality, surface, turns, chars, generation, previous_turns, previous_chars
                   from gateway_stats.session_highwater where tenant=%s and session_id = any(%s)""",
                (TENANT, sessions))
hwmap = {(s, m, su): (int(t), int(c), int(g), int(pt), int(pc)) for s, m, su, t, c, g, pt, pc in hw}

print("=== H2: did the store ADOPT A RESET on any of these rows? ===")
print("high-water rows still present for the week's buckets: %d of %d" % (len(hwmap), len(buckets)))
gens = Counter(v[2] for v in hwmap.values())
print("generation of those rows:", dict(sorted(gens.items())))
restating_present = {k: hwmap[k] for k in restating if k in hwmap}
print("restating buckets whose high-water row still exists: %d" % len(restating_present))
print("  their generations:", dict(sorted(Counter(v[2] for v in restating_present.values()).items())))
print("restating buckets whose high-water row is GONE: %d" % len([k for k in restating if k not in hwmap]))
print()

print("=== H2 across the WHOLE store, not just this week ===")
cols, g = pg.q("""select generation, count(*) from gateway_stats.session_highwater
                  where tenant=%s group by 1 order by 1""", (TENANT,))
print("every high-water row this tenant holds, by generation:", {int(a): int(b) for a, b in g})
cols, g2 = pg.q("select generation, count(*) from gateway_stats.session_highwater group by 1 order by 1")
print("every high-water row in the store, by generation:    ", {int(a): int(b) for a, b in g2})
print()

print("=== H1: does a restatement look like a FRESH INSERT? ===")
# A fresh insert appends the whole standing cumulative. So a restatement's (turns, chars) should equal
# some cumulative the walk had already reached - which is how phase one identified them - AND the row
# should be the first of a new run. Report how many restatements restate the LATEST cumulative versus an
# EARLIER one: a delete-and-reinsert restates whatever the tally stood at when the fold ran, so an
# earlier cumulative means the fold was reading a stale snapshot, not that the row reset.
lag = Counter()
for key, rs in buckets.items():
    if key not in restating: continue
    T = C = 0
    cums = [(0, 0)]
    for i, h, t, c in rs:
        if (t, c) in set(cums) and (t, c) != (0, 0):
            idx = len(cums) - 1 - cums[::-1].index((t, c))
            lag[len(cums) - 1 - idx] += 1
            continue
        T += t; C += c; cums.append((T, C))
print("a restatement restates the cumulative from N accepted rows back:")
for n, k in sorted(lag.items())[:12]:
    print("   N=%-3d %5d restatements" % (n, k))
print()

print("=== how many DISTINCT sessions, and were they alive across the restatement? ===")
print("distinct sessions that restated:", len({k[0] for k in restating}))
print("distinct sessions in the week:   ", len({k[0] for k in buckets}))
