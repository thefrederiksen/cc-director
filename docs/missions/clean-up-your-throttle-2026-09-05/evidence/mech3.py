"""Is a restatement the WHOLE standing cumulative (a fresh insert, previous_*=0) or something less?

Decisive, because the delta appended is always (new stored high-water - old stored high-water). The only
way the whole cumulative reaches stat_delta as one row is if the old stored value was ZERO - which for a
session that has been counting for hours means the row was not there at all.
"""
import sys, os
from collections import Counter, defaultdict
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pg

TENANT = '9f19679f-2e19-41a7-9acf-8cae7a8a59cc'
FROM, TO = '2026-08-24T04', '2026-08-31T04'

# The sessions that restated in the week, then their ENTIRE delta history (not week-scoped), so the
# cumulative is the session's real one.
cols, rows = pg.q("""select session_id from gateway_stats.stat_delta
                     where tenant=%s and hour_utc >= %s and hour_utc < %s group by 1""", (TENANT, FROM, TO))
sessions = [r[0] for r in rows]
cols, allrows = pg.q("""select id, session_id, modality, surface, hour_utc, turns, chars
                        from gateway_stats.stat_delta
                        where tenant=%s and session_id = any(%s) order by id""", (TENANT, sessions))
buckets = defaultdict(list)
for i, s, m, su, h, t, c in allrows:
    buckets[(s, m, su)].append((i, h, int(t), int(c)))

whole, partial, other = 0, 0, 0
examples = []
for key, rs in buckets.items():
    T = C = 0; cumset = {(0, 0)}; seen = set()
    for i, h, t, c in rs:
        if (t, c) in cumset and (t, c) != (0, 0):
            if (t, c) == (T, C): whole += 1
            else:
                partial += 1
                if len(examples) < 6: examples.append((key, i, h, t, c, T, C))
            continue
        if (h, t, c) in seen: continue
        seen.add((h, t, c)); T += t; C += c; cumset.add((T, C))

print("restatements over these sessions' WHOLE history: %d" % (whole + partial))
print("   equal to the cumulative AT THAT POINT (a fresh insert of the standing total): %d" % whole)
print("   equal to an EARLIER cumulative (a fresh insert folded from a stale snapshot): %d" % partial)
print()
print("examples of the earlier-cumulative kind (bucket, row id, hour, restated turns/chars, truth then):")
for e in examples: print("   ", e[0][1], e[0][2], "id", e[1], e[2], "restated", e[3], e[4], " truth was", e[5], e[6])
print()

# Does a restatement plus the rows immediately after it add up to exactly the standing cumulative? That is
# the signature of "the row was deleted, then two folds - a stale one and a current one - rebuilt it".
print("=== does each restatement burst re-add EXACTLY the standing cumulative, no more, no less? ===")
exact = short = over = 0
for key, rs in buckets.items():
    T = C = 0; cumset = {(0, 0)}; seen = set(); n = 0
    pend_t = pend_c = 0; in_burst = False; truth_at_burst = None
    for i, h, t, c in rs:
        is_rest = (t, c) in cumset and (t, c) != (0, 0)
        is_dup = (not is_rest) and (h, t, c) in seen
        if is_rest and not in_burst:
            in_burst = True; truth_at_burst = (T, C); pend_t = pend_c = 0
        if in_burst and (is_rest or is_dup):
            pend_t += t; pend_c += c; continue
        if in_burst:
            if (pend_t, pend_c) == truth_at_burst: exact += 1
            elif pend_t < truth_at_burst[0]: short += 1
            else: over += 1
            in_burst = False
        if is_dup: continue
        seen.add((h, t, c)); T += t; C += c; cumset.add((T, C))
    if in_burst:
        if (pend_t, pend_c) == truth_at_burst: exact += 1
        elif pend_t < truth_at_burst[0]: short += 1
        else: over += 1
print("bursts that re-added EXACTLY the standing cumulative: %d" % exact)
print("bursts that re-added LESS than it:                    %d" % short)
print("bursts that re-added MORE than it:                    %d" % over)
