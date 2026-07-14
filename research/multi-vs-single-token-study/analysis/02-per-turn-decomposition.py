import json, sys, os, glob

def turns(path):
    """Yield each assistant message's usage in order."""
    with open(path, encoding="utf-8") as f:
        for line in f:
            line=line.strip()
            if not line: continue
            try: o=json.loads(line)
            except: continue
            m=o.get("message")
            if not isinstance(m,dict): continue
            if m.get("role")!="assistant": continue
            u=m.get("usage")
            if not isinstance(u,dict): continue
            yield u

def analyze(path):
    ts=list(turns(path))
    if not ts: return None
    # context size seen by a turn = input + cache_read + cache_create (what the model read/wrote that turn)
    ctx=[ (u.get("input_tokens",0)or 0)+(u.get("cache_read_input_tokens",0)or 0)+(u.get("cache_creation_input_tokens",0)or 0) for u in ts ]
    first=ts[0]
    floor = (first.get("input_tokens",0)or 0)+(first.get("cache_read_input_tokens",0)or 0)+(first.get("cache_creation_input_tokens",0)or 0)
    out=sum(u.get("output_tokens",0)or 0 for u in ts)
    cr=sum(u.get("cache_read_input_tokens",0)or 0 for u in ts)
    cc=sum(u.get("cache_creation_input_tokens",0)or 0 for u in ts)
    inp=sum(u.get("input_tokens",0)or 0 for u in ts)
    return dict(n=len(ts), floor=floor, first_ctx=ctx[0], last_ctx=ctx[-1],
                avg_ctx=sum(ctx)//len(ctx), peak_ctx=max(ctx),
                out=out, cr=cr, cc=cc, inp=inp, total=inp+out+cc+cr)

def f(n): return f"{n:,}"

names={
 "630fc75c":"SINGLE  (did everything)",
 "420a5f7d":"multi: ARCHITECT (me, live/contaminated)",
 "d0a1f744":"multi: MANAGER",
 "279b73b4":"multi: worker",
 "5f42307a":"multi: worker",
 "690973a6":"multi: worker",
}
for label, pat in [("SINGLE", sys.argv[1]),("MULTI", sys.argv[2])]:
    print(f"\n===== {label} =====")
    print(f"{'session':<40} {'turns':>5} {'startFloor':>11} {'avgCtx':>9} {'peakCtx':>9} {'output':>8} {'TOTAL':>12}")
    for p in sorted(glob.glob(pat)):
        a=analyze(p)
        if not a: continue
        key=os.path.basename(p)[:8]
        nm=names.get(key,key)
        print(f"{nm:<40} {a['n']:>5} {f(a['floor']):>11} {f(a['avg_ctx']):>9} {f(a['peak_ctx']):>9} {f(a['out']):>8} {f(a['total']):>12}")
