"""Phase two, task 1 - the SHAPE of the 594 terminal-typed turns Your Throttle never counts.

Reads only. Prints counts and lengths. It prints no prompt prose: the only text it prints is the
exact lowercase token of records at or under SHORT_TOKEN_CHARS characters, which is what the
question "are these bare confirmations or composed prompts" is actually asking.
"""
import sys, os, datetime as dt, statistics
from collections import Counter
MENTOR = r"D:/ReposFred/devthrottle_internal-throttle/tools/mentor"
sys.path.insert(0, MENTOR); os.chdir(MENTOR)
import metrics, origin as origin_module
from pathlib import Path

SHORT_TOKEN_CHARS = 12

# --- record which ledger event each prompt claimed -------------------------------------------
CLAIMS = {}
COLLISIONS = []
_orig = origin_module.Ledger.nearest_unclaimed
def patched(self, session, ts, want=None):
    row = _orig(self, session, ts, want)
    key = (session, ts)
    if key in CLAIMS:
        COLLISIONS.append(key)
    CLAIMS[key] = row
    return row
origin_module.Ledger.nearest_unclaimed = patched

ROOT = Path(r"D:/Personal/OneDrive/Center Consulting/DevThrottle/mentor-data/accounts/soren")
paths = {"prompt_log": ROOT/"raw"/"prompt-log", "db": ROOT/"raw"/"db"}
ends = {s: dt.datetime(2026,9,1,22,27,6, tzinfo=dt.timezone.utc) for s in metrics.SOURCE_NAMES}
world = metrics.load_world("soren", "America/Toronto", paths, ends)
week = metrics.Week(world, "2026-W35", {"start":8,"end":18})

print("week:", week.start, "->", week.end)
print("user records:", len(week.user_prompts), " human:", len(week.human_prompts))
print("claim lookups recorded:", len(CLAIMS), " key collisions:", len(COLLISIONS))
print()

# --- the ledger's own population, for the denominator ------------------------------------------
led = origin_module.Ledger(world.events)
inweek = [r for rows in led.by_session.values() for r in rows if week.start <= r["ts"] < week.end]
null_src = [r for r in inweek if r["source"] is None and r["origin"] is not None]
print("ledger turn-submitted in week:", len(inweek))
print("  SendSource NULL carrying an origin (the SendInput population):", len(null_src),
      dict(Counter(r["origin"][0]+"/"+r["origin"][1] for r in null_src)))
print()

# --- the human prompts whose claimed event is one of those -------------------------------------
sendinput, composer, other = [], [], []
for p in week.human_prompts:
    ev = CLAIMS.get((p["session"], p["ts"]))
    if ev is None or ev["origin"] is None:
        other.append(p); continue
    if ev["source"] is None:
        sendinput.append(p)
    elif ev["source"] == "UserInput":
        composer.append(p)
    else:
        other.append(p)

print("human prompts matched to a SendInput (SendSource null) event:", len(sendinput))
print("   modality/surface:", dict(Counter((p["origin_modality"], p["origin_surface"]) for p in sendinput)))
print("human prompts matched to a UserInput event:", len(composer),
      dict(Counter((p["origin_modality"], p["origin_surface"]) for p in composer)))
print("human prompts matched to something else / no claim:", len(other))
print()

typed = [p for p in sendinput if p["origin_modality"] == "typed"]
print("=== THE POPULATION: typed turns through Session.SendInput, with text ===")
print("count:", len(typed))
lens = sorted(len(p["text"]) for p in typed)
words = sorted(p["words"] for p in typed)

def pct(xs, q):
    if not xs: return 0
    i = (len(xs)-1) * q
    lo, hi = int(i), min(int(i)+1, len(xs)-1)
    return xs[lo] + (xs[hi]-xs[lo]) * (i-lo)

print()
print("characters per turn:")
print("   min %d  p10 %.0f  p25 %.0f  MEDIAN %.0f  p75 %.0f  p90 %.0f  p99 %.0f  max %d"
      % (lens[0], pct(lens,.10), pct(lens,.25), pct(lens,.50), pct(lens,.75), pct(lens,.90), pct(lens,.99), lens[-1]))
print("   mean %.1f   total %d" % (statistics.mean(lens), sum(lens)))
print("words per turn:")
print("   min %d  p10 %.0f  MEDIAN %.0f  p90 %.0f  max %d  total %d"
      % (words[0], pct(words,.10), pct(words,.50), pct(words,.90), words[-1], sum(words)))
print()
buckets = [(0,4),(5,9),(10,19),(20,49),(50,99),(100,199),(200,499),(500,999),(1000,10**9)]
print("distribution by characters:")
for lo,hi in buckets:
    n = sum(1 for x in lens if lo <= x <= hi)
    label = ("%d-%d" % (lo,hi)) if hi < 10**9 else ("%d+" % lo)
    print("   %-10s %5d  %5.1f%%  %s" % (label, n, 100.0*n/len(lens), "#"*int(60.0*n/len(lens))))
print()
under5 = sum(1 for x in lens if x < 5)
under12 = sum(1 for x in lens if x <= SHORT_TOKEN_CHARS)
atleast20 = sum(1 for x in lens if x >= 20)
print("UNDER 5 CHARACTERS: %d  (%.1f%%)" % (under5, 100.0*under5/len(lens)))
print("at most %d characters: %d  (%.1f%%)" % (SHORT_TOKEN_CHARS, under12, 100.0*under12/len(lens)))
print("20 characters or more: %d  (%.1f%%)" % (atleast20, 100.0*atleast20/len(lens)))
print("2 words or more: %d  (%.1f%%)" % (sum(1 for w in words if w>=2), 100.0*sum(1 for w in words if w>=2)/len(words)))
print()
print("the short records (<= %d characters), by exact lowercase token:" % SHORT_TOKEN_CHARS)
short = Counter(p["text"].strip().lower() for p in typed if len(p["text"]) <= SHORT_TOKEN_CHARS)
for tok, n in sorted(short.items(), key=lambda kv: -kv[1]):
    print("   %5d  %r" % (n, tok))
print()
print("sessions represented:", len({p["session"] for p in typed}))
print("origin rule split:", dict(Counter(p["origin_rule"] for p in typed)))
