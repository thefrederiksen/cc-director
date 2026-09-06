"""Snapshot the live high-water key set, so a second snapshot can say whether rows VANISH for sessions
that are still counting. That is the one thing the historical rows cannot tell us."""
import sys, os, json, time, datetime
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pg
TENANT = '9f19679f-2e19-41a7-9acf-8cae7a8a59cc'
cols, hw = pg.q("""select session_id, modality, surface, turns, chars, generation
                   from gateway_stats.session_highwater where tenant=%s""", (TENANT,))
cols, mx = pg.q("select max(id) from gateway_stats.stat_delta where tenant=%s", (TENANT,))
snap = {"at": datetime.datetime.utcnow().isoformat(), "max_delta_id": int(mx[0][0]),
        "rows": {"|".join([s, m, su]): [int(t), int(c), int(g)] for s, m, su, t, c, g in hw}}
out = sys.argv[1]
json.dump(snap, open(out, "w"))
print(out, "rows:", len(snap["rows"]), "max stat_delta id:", snap["max_delta_id"], "at", snap["at"])
