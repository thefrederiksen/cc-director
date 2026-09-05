"""Forget deletes EVERY bucket of a session at once (it keys on session id, not on modality/surface).
So if the mechanism is a delete, a session with more than one bucket must restate ALL of them together.
A per-bucket cause could not do that."""
import sys, os
from collections import Counter, defaultdict
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pg

TENANT = '9f19679f-2e19-41a7-9acf-8cae7a8a59cc'
FROM, TO = '2026-08-24T04', '2026-08-31T04'
cols, rows = pg.q("""select id, session_id, modality, surface, hour_utc, turns, chars
                     from gateway_stats.stat_delta
                     where tenant=%s and hour_utc >= %s and hour_utc < %s order by id""", (TENANT, FROM, TO))
buckets = defaultdict(list)
for i, s, m, su, h, t, c in rows:
    buckets[(s, m, su)].append((i, h, int(t), int(c)))

# The first restatement row id of each bucket, and the bucket's own row ids.
first_rest = {}
bucket_ids = {}
for key, rs in buckets.items():
    bucket_ids[key] = [r[0] for r in rs]
    T = C = 0; cumset = {(0, 0)}; seen = set()
    for i, h, t, c in rs:
        if (t, c) in cumset and (t, c) != (0, 0):
            first_rest.setdefault(key, i); continue
        if (h, t, c) in seen: continue
        seen.add((h, t, c)); T += t; C += c; cumset.add((T, C))

by_session = defaultdict(list)
for key in buckets: by_session[key[0]].append(key)

multi = {s: ks for s, ks in by_session.items() if len(ks) > 1}
print("sessions with more than one bucket in the week: %d" % len(multi))
both_rest = 0; only_one = 0; none_rest = 0; together = 0; apart = []
for s, ks in multi.items():
    r = [k for k in ks if k in first_rest]
    if not r: none_rest += 1; continue
    if len(r) == len(ks):
        both_rest += 1
        ids = sorted(first_rest[k] for k in r)
        # "Together" = the first restatements of every bucket fall inside one contiguous stretch of
        # this session's own delta rows, with no accepted row of any of those buckets in between.
        span = ids[-1] - ids[0]
        allids = sorted(i for k in ks for i in bucket_ids[k])
        between = [i for i in allids if ids[0] < i < ids[-1]]
        if len(between) <= len(ids): together += 1
        else: apart.append((s, ids, len(between)))
    else:
        only_one += 1
print("  every bucket of the session restated:      %d" % both_rest)
print("     ...and their first restatements arrive as one contiguous burst: %d" % together)
print("     ...spread apart instead:                                       %d" % len(apart))
print("  only some buckets restated:                %d" % only_one)
print("  no bucket restated:                        %d" % none_rest)
print()
print("examples of the spread-apart kind (session, first-restatement row ids, rows in between):")
for a in apart[:8]: print("   ", a[0][:8], a[1], a[2])
