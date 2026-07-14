import json, sys, os

def summarize(path):
    inp=out=cc=cr=0
    msgs=0
    model=set()
    with open(path, encoding="utf-8") as f:
        for line in f:
            line=line.strip()
            if not line: continue
            try: o=json.loads(line)
            except: continue
            m=o.get("message")
            if not isinstance(m,dict): continue
            u=m.get("usage")
            if not isinstance(u,dict): continue
            inp+=u.get("input_tokens",0) or 0
            out+=u.get("output_tokens",0) or 0
            cc+=u.get("cache_creation_input_tokens",0) or 0
            cr+=u.get("cache_read_input_tokens",0) or 0
            msgs+=1
            if m.get("model"): model.add(m["model"])
    return dict(inp=inp,out=out,cc=cc,cr=cr,msgs=msgs,model=",".join(sorted(model)))

def fmt(n): return f"{n:,}"

for label, d in [("=== "+sys.argv[1]+" ===", sys.argv[2:])]:
    pass

group=sys.argv[1]
files=sys.argv[2:]
tot=dict(inp=0,out=0,cc=0,cr=0,msgs=0)
print(f"\n########## {group} ##########")
rows=[]
for p in files:
    s=summarize(p)
    for k in tot: tot[k]+=s[k]
    name=os.path.basename(p)[:8]
    print(f"  {name}  turns={s['msgs']:>4}  out={fmt(s['out']):>9}  in={fmt(s['inp']):>7}  cacheCreate={fmt(s['cc']):>10}  cacheRead={fmt(s['cr']):>12}")
billable = tot['inp']+tot['out']+tot['cc']+tot['cr']
print(f"  ---")
print(f"  TOTAL turns={tot['msgs']}  output={fmt(tot['out'])}  input={fmt(tot['inp'])}  cacheCreate={fmt(tot['cc'])}  cacheRead={fmt(tot['cr'])}")
print(f"  GRAND TOTAL tokens (in+out+cacheCreate+cacheRead) = {fmt(billable)}")
