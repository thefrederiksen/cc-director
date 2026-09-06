"""Do restatements cluster on a DIRECTOR, at a moment? That is the signature of a Director restart
deleting its live sessions' high-water rows and then re-registering the same sessions with their
persisted tallies intact."""
import sys, os, datetime as dt
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
rest = []
for (s, m, su), rs in buckets.items():
    T = C = 0; cumset = {(0, 0)}; seen = set()
    for i, h, t, c in rs:
        if (t, c) in cumset and (t, c) != (0, 0):
            rest.append((i, h, s)); continue
        if (h, t, c) in seen: continue
        seen.add((h, t, c)); T += t; C += c; cumset.add((T, C))

sess = sorted({r[2] for r in rest})
cols, hrows = pg.q("""select "SessionId","DirectorId","MachineName","StartedAtUtc","EndedAtUtc","OriginKind",
                             "ParentSessionId","DirectorVersion"
                      from gateway.session_history where tenant_id=%s and "SessionId" = any(%s)""",
                   (TENANT, sess))
info = {r[0]: r[1:] for r in hrows}
print("restating sessions with a history row: %d of %d" % (len(info), len(sess)))
print("their origin kinds:", dict(Counter(v[4] for v in info.values())))
print()

# Cluster: (director, hour) -> distinct restating sessions
byd = defaultdict(set)
for i, h, s in rest:
    d = info.get(s, (None,))[0]
    byd[(d, h)].add(s)
sizes = Counter(len(v) for v in byd.values())
print("(director, hour) groups by how many DISTINCT sessions restated in them:", dict(sorted(sizes.items())))
print("groups with 3 or more sessions restating at once:")
for k, v in sorted(byd.items(), key=lambda kv: -len(kv[1]))[:12]:
    print("   director=%s hour=%s sessions=%d" % (str(k[0])[:8], k[1], len(v)))
print()

# How many distinct Directors ran that week, and how many restated?
print("distinct directors among restating sessions:", len({v[0] for v in info.values()}))
cols, alld = pg.q("""select "DirectorId", count(*) from gateway.session_history
                     where tenant_id=%s and "StartedAtUtc" >= %s and "StartedAtUtc" < %s group by 1 order by 2 desc""",
                  (TENANT, dt.datetime(2026,8,24,4,tzinfo=dt.timezone.utc), dt.datetime(2026,8,31,4,tzinfo=dt.timezone.utc)))
print("directors that started a session in the week:", len(alld))
for d, n in alld[:8]: print("   %s  %d sessions" % (str(d)[:8], n))
