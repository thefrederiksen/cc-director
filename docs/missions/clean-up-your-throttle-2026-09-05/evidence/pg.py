import re, psycopg2, os
SC = os.path.dirname(os.path.abspath(__file__))
raw = open(os.path.join(SC,'pgconn.txt')).read().strip()
kv = dict()
for part in raw.split(';'):
    if '=' in part:
        k,v = part.split('=',1); kv[k.strip().lower()] = v.strip()
def connect():
    return psycopg2.connect(host=kv['host'], port=int(kv['port']), dbname=kv['database'],
                            user=kv['username'], password=kv['password'], sslmode='require')
def q(sql, args=None):
    with connect() as c, c.cursor() as cur:
        cur.execute(sql, args or ())
        cols = [d[0] for d in cur.description]
        return cols, cur.fetchall()
if __name__ == '__main__':
    import sys
    cols, rows = q(sys.stdin.read())
    print(' | '.join(cols))
    for r in rows: print(' | '.join('' if x is None else str(x) for x in r))
